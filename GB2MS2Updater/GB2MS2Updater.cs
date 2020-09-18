using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SerialCAN;

namespace GB2MS2Updater
{

    /* Heavily based on the work of https://github.com/GBert/railroad/blob/master/can2udp/src/gb2-update.c
     * replicated copyright notice from link above: 
      *  * ----------------------------------------------------------------------------
         * "THE BEER-WARE LICENSE" (Revision 42):
         * <info@gerhard-bertelsmann.de> wrote this file. As long as you retain this notice you
         * can do whatever you want with this stuff. If we meet some day, and you think
         * this stuff is worth it, you can buy me a beer in return Gerhard Bertelsmann
         * ----------------------------------------------------------------------------
         * 
         /* the hard analyzing work was done by Karsten - Thx //
     */

    public class UpdateFile
    {
        public string name;
        public string versionName;
        public string fileName;
        public VersionType versionType;
        public int blockSize;
        public int blockOffset;
        public int fillUpto;
        public byte paddingByte;
        public int fileVersionIndex;

        public UpdateFile(string name, string versionName, string fileName, VersionType versionType, int blockSize, int blockOffset, int fillUpto, byte paddingByte, int fileVersionIndex)
        {
            this.name = name;
            this.versionName = versionName;
            this.fileName = fileName;
            this.versionType = versionType;
            this.blockSize = blockSize;
            this.blockOffset = blockOffset;
            this.fillUpto = fillUpto;
            this.paddingByte = paddingByte;
            this.fileVersionIndex = fileVersionIndex;
        }
    };

    public enum VersionType
    {
        Default = 0,
        Db = 1,
    }

    public enum DeviceType
    { 
        MS2 = 0x4d,
        GB2 = 0x47,
    }
    public class GB2MS2Updater
    {
        private SerialCAN.SerialCAN serialCAN = null;

        private readonly UpdateFile[] gb2_update_data = new UpdateFile[]
        {
            new UpdateFile("gbs2", "gb2",       "016-gb2.bin", VersionType.Default,      512,        2,    512,  0xff, 6),
        };

        private readonly UpdateFile[] ms2_update_data = new UpdateFile[]
        {
            new UpdateFile("ms2",   "ms2ver",   "050-ms2.bin",  VersionType.Default,     1024,       4,   1024,  0xff, 252),
            new UpdateFile("gb2",   "gb2ver",   "016-gb2.bin",  VersionType.Default,     1024,       0,      8,  0x00, 6),
            new UpdateFile("lokdb",   "ldbver",   "flashdb.ms2",  VersionType.Db,          1024,       0,      8,  0x00, 0),
            new UpdateFile("lang",  "langver",  "lang.ms2",     VersionType.Default,     1024,       0,      8,  0x00, 0),
            new UpdateFile("mfxdefs", "mfxver",   "mfxdefs.ms2",  VersionType.Default,     1024,       0,      8,  0x00, 0),
            new UpdateFile("mfxbin",  "mfxbver",  "mfxdefs.bin",  VersionType.Default,     1024,       0,      8,  0x00, 0),
            new UpdateFile("ms2x",  "ms2xver",  "051-ms2.bin",  VersionType.Default,     1024,       0,      8,  0x00, 0),
        };

        enum CANMaerklinCommand
        {
            Reset = 0x000000,
            Ping = 0x300000,
            PingRsp = 0x310000,
            Bootloader = 0x360000,
            BootloaderRsp = 0x370000,
            ConfigDataQuery = 0x400000,
            ConfigDataQueryRsp = 0x410000,
            ConfigDataStream = 0x420000,
            ConfigDataStreamRsp = 0x430000
        }

        //We will use some arbitrary number as "CS" address hash
        static private readonly UInt32 CS2AddressHash = 0x4711;

        private UpdateFile[] CurrentUpdateFiles = null;
        private int CurrentUpdateFileIndex = 0;
        byte[] DataBytesToSendWithPadding = null;
        byte[] FileBytes = null;
        private int FileLength = 0;
        private string CurrentDeviceId = string.Empty;
        private CANMessage CheckFrame = null;
        private CANMessage CheckFrameNack = null;
        private CANMessage CheckFrameBlockId = null;
        private CANMessage LastSentFrame = null;
        private CANMessage AckconfigFileRequestFrame = null;
        private string CurrentLoadedFileName;
        private UInt32 CurrentDeviceHash = 0;

        private int LastBinBlock { get; set; }
        private int TotalBlocksToSend { get; set; }

        public bool UpdateRunning { get; set; }

        private string FirmwareFilePath { get; set; }
        private string FirmwareFileVersion { get; set; }

        private bool ForceUpdate { get; set; }

        private bool PingReceived { get; set; }

        private AutoResetEvent UpdateCompletedAutoResetEvent = new AutoResetEvent(false);

        private bool IsFirmwareUpdate = false;
        private bool FileUpdateRunning = false;

        private DeviceType DeviceType;

        private int CurrentFileVHigh { get { return FileBytes[CurrentUpdateFiles[CurrentUpdateFileIndex].fileVersionIndex]; } }
        private int CurrentFileVLow { get { return FileBytes[CurrentUpdateFiles[CurrentUpdateFileIndex].fileVersionIndex + 1]; } }

        private string RequestedConfigNameData { get; set; }

        private bool Verbose { get; set; }

        private static UInt32 BuildCANId(CANMaerklinCommand command, UInt32 addressHash)
        {
            return (UInt32)command + addressHash;
        }

        public GB2MS2Updater(string portName, string firmwareFilesPath, DeviceType deviceType, bool verbose)
        {
            this.FirmwareFilePath = firmwareFilesPath;
            this.DeviceType = deviceType;

            if (!Directory.Exists(firmwareFilesPath))
            {
                throw new FileNotFoundException("Path not found");
            }

            switch (deviceType)
            {
                case DeviceType.GB2:
                    CurrentUpdateFiles = gb2_update_data;
                    break;
                case DeviceType.MS2:
                    CurrentUpdateFiles = ms2_update_data;
                    break;
                default:
                    throw new NotSupportedException();
            }

            serialCAN = new SerialCAN.SerialCAN(portName, 230400, verbose);
            serialCAN.CANMessageReceived += SerialCAN_CANMessageReceived;
            serialCAN.ConfigureCANBitrate(CANBitrate.Kbit250);
            serialCAN.OpenCAN(true);
        }

        public string GetCurrentCS2FileVersionInfo(int fileLength)
        {
            StringBuilder sb = new StringBuilder();
            switch (CurrentUpdateFiles[CurrentUpdateFileIndex].versionType)
            {
                case VersionType.Default:
                    sb.AppendFormat(" .vhigh={0}\n", CurrentFileVHigh);
                    sb.AppendFormat(" .vlow={0}\n", CurrentFileVLow);
                    sb.AppendFormat(" .bytes={0}\n", fileLength);
                    break;
                
                case VersionType.Db:
                    //constant blocksize 0x40
                    //We expect to have version information in last block 
                    var versionBytes = FileBytes.Skip(FileBytes.Length - 0x40).ToArray();
                    sb.AppendFormat(" .version={0}\n", UInt16.Parse(Encoding.ASCII.GetString(versionBytes.Skip(0x10).Take(3).ToArray())));
                    sb.AppendFormat(" .monat={0}\n", UInt16.Parse(Encoding.ASCII.GetString(versionBytes.Skip(0x0c).Take(2).ToArray())));
                    sb.AppendFormat(" .jahr={0}\n", UInt16.Parse(Encoding.ASCII.GetString(versionBytes.Skip(0x07).Take(4).ToArray())));
                    //Exclude header (length 0x40)
                    sb.AppendFormat(" .anzahl={0}\n", (fileLength / 0x40) - 1);
                    break;
                
                default:
                    throw new NotSupportedException();
            }

            return sb.ToString();
        }

        public void UpdateMS2Files()
        {
            /* send CAN Ping */
            if (DeviceType != DeviceType.MS2)
            {
                throw new NotSupportedException();
            }

            UpdateRunning = true;
            Console.WriteLine("Send Ping");
            serialCAN.SendCAN(new CANMessage(BuildCANId(CANMaerklinCommand.Ping, CS2AddressHash), new byte[0]));
            
            IsFirmwareUpdate = false;
            //Todo: create real timeout handling
            bool success = UpdateCompletedAutoResetEvent.WaitOne(3600000);
            serialCAN.CloseCAN();
        }

        public void StartUpdate(bool forceUpdate)
        {
            IsFirmwareUpdate = true;
            CurrentUpdateFileIndex = 0;
            ReadFirmwareFile(CurrentUpdateFiles[CurrentUpdateFileIndex], true);

            this.ForceUpdate = forceUpdate;

            UpdateRunning = true;
            LastBinBlock = 0;

            /* send CAN Ping */
            Console.WriteLine("Send Ping");
            serialCAN.SendCAN(new CANMessage(BuildCANId(CANMaerklinCommand.Ping, CS2AddressHash), new byte[0]));


            if (DeviceType == DeviceType.GB2)
            {
                /* start Maerklin 60113 box */
                serialCAN.SendCAN(new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, 0x0301), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x11 }));
            }

            //Todo: create real timeout handling
            bool success = UpdateCompletedAutoResetEvent.WaitOne(3600000);
            serialCAN.CloseCAN();
        }

        /// <summary>
        /// Create an array where all bytes are set to given value
        /// </summary>
        /// <param name="length"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private static byte[] InitializeArray(int length, byte value)
        {
            var arr = new byte[length];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = value;
            }
            return arr;
        }

        private void ReadFirmwareFile(UpdateFile updateFile, bool setVersion)
        {
            if (updateFile.fileName != CurrentLoadedFileName)
            {
                CurrentLoadedFileName = updateFile.fileName;
                FileBytes = File.ReadAllBytes(Path.Combine(FirmwareFilePath, updateFile.fileName));
                DataBytesToSendWithPadding = FileBytes;
                FileLength = DataBytesToSendWithPadding.Length;

                Console.WriteLine("Loading {0} with size {1}", updateFile.fileName, FileLength);

                /* prepare padding */
                int byteCountToSend = (int)((FileLength + (updateFile.fillUpto - 1)) & (0xFFFFFFFF - (updateFile.fillUpto - 1)));

                if (byteCountToSend != DataBytesToSendWithPadding.Length)
                {
                    /* create a new padded array */
                    var newBytes = InitializeArray(byteCountToSend, updateFile.paddingByte);
                    Array.Copy(DataBytesToSendWithPadding, 0, newBytes, 0, DataBytesToSendWithPadding.Length);
                    DataBytesToSendWithPadding = newBytes;
                }

                TotalBlocksToSend = (byteCountToSend - 1) / updateFile.blockSize;

                if (setVersion)
                {
                    FirmwareFileVersion = string.Format("{0}.{1}", CurrentFileVHigh, CurrentFileVLow);
                }
            }
        }

        private void PrepareCS2FileVersionInfoToSend(UpdateFile updateFile)
        {
            FileBytes = File.ReadAllBytes(Path.Combine(FirmwareFilePath, updateFile.fileName));
            var versionInfoString = GetCurrentCS2FileVersionInfo(FileBytes.Length);
            DataBytesToSendWithPadding = Encoding.ASCII.GetBytes(versionInfoString);
            FileLength = DataBytesToSendWithPadding.Length;

            Console.WriteLine("Loading data with size {0}", FileLength);

            /* prepare padding */
            int byteCountToSend = (int)((FileLength + (8 - 1)) & (0xFFFFFFFF - (8 - 1)));

            if (byteCountToSend != DataBytesToSendWithPadding.Length)
            {
                /* create a new padded array */
                var newBytes = InitializeArray(byteCountToSend, 0x00);
                Array.Copy(DataBytesToSendWithPadding, 0, newBytes, 0, DataBytesToSendWithPadding.Length);
                DataBytesToSendWithPadding = newBytes;
            }

            TotalBlocksToSend = (byteCountToSend - 1) / updateFile.blockSize;
        }

        private string GetDeviceIdFromCanMessage(CANMessage canMessage)
        {
            if (canMessage.Data.Length == 8)
            {
                return canMessage.Data.Take(4).ToArray().ByteArrayToHexString();
            }
            else
            {
                return string.Empty;
            }
        }

        void SendNextBlockId(byte block)
        {
            LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x44, 0x00 });
            Array.Copy(CurrentDeviceId.HexStringToByteArray(), 0, LastSentFrame.Data, 0, 4);
            LastSentFrame.Data[5] = block;
            serialCAN.SendCAN(LastSentFrame);

            CheckFrameBlockId = CheckFrame.Clone();
            CheckFrameBlockId.Data = new byte[6];
            Array.Copy(CheckFrame.Data, 0, CheckFrameBlockId.Data, 0, min(CheckFrame.Data.Length, 6));
            CheckFrameBlockId.Data[4] = 0x44;
            CheckFrameBlockId.Data[5] = block;
        }
        /// <summary>
        /// returns the smaller value of given integers
        /// </summary>
        /// <param name="i"></param>
        /// <param name="j"></param>
        /// <returns></returns>
        private static int min(int i, int j)
        {
            if (i < j)
            {
                return i;
            }
            else
            {
                return j;
            }
        }

        void SendFirmwareBlock(byte[] data, int length)
        {
            int i = 0;
            int part = 0;
            byte[] crc16;

            Console.WriteLine("sending block 0x{0:X02} 0x{1:X04} 0x{2:X04}", LastBinBlock + CurrentUpdateFiles[CurrentUpdateFileIndex].blockOffset, LastBinBlock * CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize, length);

            for (i = 0; i < length; i += 8)
            {
                //build Message Id containing current part number
                LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, 0x0300) + (byte)part, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x88, 0x00, 0x00, 0x00 });
                part++;
                Array.Copy(data, i, LastSentFrame.Data, 0, 8);
                serialCAN.SendCAN(LastSentFrame);
            }

            LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x88, 0x00, 0x00 });
            Array.Copy(CurrentDeviceId.HexStringToByteArray(), 0, LastSentFrame.Data, 0, 4);
            Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.NonZero1);
            crc16 = crc16Ccitt.ComputeChecksumBytes(data.Take(length).ToArray()).ToBigEndian();
            LastSentFrame.Data[5] = crc16[0];
            LastSentFrame.Data[6] = crc16[1];
            Console.WriteLine("block checksum 0x{0}", crc16.ByteArrayToHexString());
            serialCAN.SendCAN(LastSentFrame);
        }

        private void StartConfigFileSend()
        {
            //advertise as CS2
            serialCAN.SendCAN(new CANMessage(BuildCANId(CANMaerklinCommand.PingRsp, CS2AddressHash), new byte[] { 0x43, 0x53, 0x2D, 0x32, 0x20, 0x20, 0xff, 0xff }));
        }

        private void StartFirmwareUpdateProcedure(string deviceVersion)
        {
            if (ForceUpdate || deviceVersion != FirmwareFileVersion)
            {
                Console.WriteLine("Starting update to {0}", FirmwareFileVersion);
                var canMessage = new CANMessage(BuildCANId(CANMaerklinCommand.Reset, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x80, 0xff });
                Array.Copy(CurrentDeviceId.HexStringToByteArray(), 0, canMessage.Data, 0, 4);
                serialCAN.SendCAN(canMessage);
                /* delay for boot ? */
                Thread.Sleep(500);
                serialCAN.SendCAN(new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, CS2AddressHash), new byte[0]));
            }
            else
            {
                Console.WriteLine("Firmware already installed.");
                UpdateCompletedAutoResetEvent.Set();
                UpdateRunning = false;
            }
        }

        private void SerialCAN_CANMessageReceived(object sender, CANMessageReceivedEventArgs e)
        {
            int requestedBlockIndex;
            if (UpdateRunning)
            {
                switch ((CANMaerklinCommand)(e.CANMessage.Id & 0xFFFF0000UL))
                {
                    case (CANMaerklinCommand.Ping):
                        Console.WriteLine("received CAN Ping {0}\n", e.CANMessage);
                        if (CurrentDeviceHash == (e.CANMessage.Id & 0x00FFFF))
                        {
                            if (FileUpdateRunning)
                            {
                                //Pin received from update device so we expect the File update has been completed
                                Console.WriteLine("UPDATE COMPLETE");

                                UpdateCompletedAutoResetEvent.Set();
                                UpdateRunning = false;
                            }
                        }
                        break;

                    case (CANMaerklinCommand.PingRsp):
                        Console.WriteLine("received CAN Ping answer from Device {0}\n", (DeviceType)(e.CANMessage.Data[0]));
                        if ((e.CANMessage.Data.Length == 8) && (e.CANMessage.Data[0] == (byte)DeviceType))
                        {
                            string deviceVersion = string.Format("{0}.{1}", e.CANMessage.Data[4], e.CANMessage.Data[5]);

                            switch ((DeviceType)e.CANMessage.Data[0])
                            {
                                case DeviceType.GB2:
                                    Console.WriteLine("found Gleisbox with ID 0x{0} Version {1}\n", CurrentDeviceId, deviceVersion);
                                    CurrentDeviceId = GetDeviceIdFromCanMessage(e.CANMessage);
                                    CurrentDeviceHash = e.CANMessage.Id & 0x00FFFF;
                                    PingReceived = true;
                                    break;
                                case DeviceType.MS2:
                                    Console.WriteLine("found MS2 with ID 0x{0} Version {1}\n", CurrentDeviceId, deviceVersion);
                                    CurrentDeviceId = GetDeviceIdFromCanMessage(e.CANMessage);
                                    CurrentDeviceHash = e.CANMessage.Id & 0x00FFFF;
                                    PingReceived = true;
                                    break;
                            }

                            if (IsFirmwareUpdate)
                            {
                                StartFirmwareUpdateProcedure(deviceVersion);
                            }
                            else
                            {
                                StartConfigFileSend();
                            }
                        }
                        break;

                    case CANMaerklinCommand.BootloaderRsp:
                        //Firmware update process
                        if (PingReceived)
                        {
                            if (e.CANMessage.Data.Length == 8 && GetDeviceIdFromCanMessage(e.CANMessage) == CurrentDeviceId && ((e.CANMessage.Data[7] == 0x10) || (e.CANMessage.Data[7] == 0x32)))
                            {
                                Console.WriteLine("Send initial block id");
                                SendInitialBlockId(e.CANMessage);
                            }
                            else
                            {
                                /* first data block */
                                if (LastSentFrame == null || e.CANMessage.Data.ByteArrayEquals(LastSentFrame.Data) && LastBinBlock == TotalBlocksToSend)
                                {
                                    Console.WriteLine("Send first data block");
                                    SendFirmwareBlock(DataBytesToSendWithPadding.Skip(LastBinBlock * (int)CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize).ToArray(), DataBytesToSendWithPadding.Length - TotalBlocksToSend * (int)CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize);
                                    LastBinBlock--;
                                }
                                else
                                {
                                    //there seems to be different NACK types : 0xF1 and 0xF2 
                                    if (CheckFrameNack != null && e.CANMessage.Data.Length >= 5 && CheckFrameNack.Data.Length >= 5
                                        && e.CANMessage.Id == CheckFrameNack.Id && e.CANMessage.Data.Take(4).ToArray().ByteArrayEquals(CheckFrameNack.Data.Take(4).ToArray())
                                        && (e.CANMessage.Data[4] == 0xF1 || e.CANMessage.Data[4] == 0xF2)
                                        )
                                    {
                                        Console.WriteLine("Aiiee got NACK. Aborting");
                                        UpdateCompletedAutoResetEvent.Set();
                                        UpdateRunning = false;
                                    }

                                    //MS2 may use 0x0000 as hash -> compare CheckFrameBlockId full 8 bytes
                                    if (CheckFrameBlockId != null && e.CANMessage.Data.ByteArrayEquals(CheckFrameBlockId.Data))
                                    {
                                        SendFirmwareBlock(DataBytesToSendWithPadding.Skip(LastBinBlock * (int)CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize).ToArray(), (int)CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize);
                                        //firmware flashing has to be sent in reversed order
                                        LastBinBlock--;
                                    }

                                    //MS2 may use 0x0000 as hash -> compare CheckFrame full 8 bytes
                                    if (CheckFrame != null && e.CANMessage.Data.Length == CheckFrame.Data.Length && e.CANMessage.Data.Take(5).ToArray().ByteArrayEquals(CheckFrame.Data.Take(5).ToArray()))
                                    {
                                        if ((LastBinBlock >= 0))
                                        {
                                            SendNextBlockId((byte)(LastBinBlock + CurrentUpdateFiles[CurrentUpdateFileIndex].blockOffset));
                                        }
                                        else
                                        {
                                            if (DeviceType == DeviceType.MS2)
                                            {
                                                Console.WriteLine("Reboot MS2 - be patient...");
                                                //end of update
                                                LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0xf5 });
                                                Array.Copy(CurrentDeviceId.HexStringToByteArray(), 0, LastSentFrame.Data, 0, 4);
                                                serialCAN.SendCAN(LastSentFrame);
                                                Thread.Sleep(1000);
                                                //soft reset
                                                LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.Bootloader, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x11 });
                                                Array.Copy(CurrentDeviceId.HexStringToByteArray(), 0, LastSentFrame.Data, 0, 4);
                                                serialCAN.SendCAN(LastSentFrame);
                                                Thread.Sleep(13000);

                                                //Start updating MS2 files
                                                StartConfigFileSend();
                                            }
                                            else
                                            {
                                                Console.WriteLine("UPDATE COMPLETE");

                                                UpdateCompletedAutoResetEvent.Set();
                                                UpdateRunning = false;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case CANMaerklinCommand.ConfigDataQuery:
                        //Send/Update CONFIG DATA

                        if (e.CANMessage.Data.ByteArrayToHexString() == "6666666666666666")
                        {
                            if (RequestedConfigNameData.EndsWith("ver"))
                            {
                                if (AckconfigFileRequestFrame != null)
                                {
                                    serialCAN.SendCAN(AckconfigFileRequestFrame);
                                }

                                //Send config data

                                LastBinBlock = 0;
                                PrepareCS2FileVersionInfoToSend(CurrentUpdateFiles[CurrentUpdateFileIndex]);
                                byte[] fileSizeBytes = BitConverter.GetBytes(FileLength).ToBigEndian();
                                Array.Copy(fileSizeBytes, LastSentFrame.Data, 4);
                                Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.NonZero1);
                                LastSentFrame.Id = BuildCANId(CANMaerklinCommand.ConfigDataStream, e.CANMessage.Id & 0x00FFFF);
                                var crc16 = crc16Ccitt.ComputeChecksumBytes(DataBytesToSendWithPadding).ToBigEndian();
                                LastSentFrame.Data[4] = crc16[0];
                                LastSentFrame.Data[5] = crc16[1];
                                Console.WriteLine("block checksum 0x{0}", crc16.ByteArrayToHexString());
                                serialCAN.SendCAN(LastSentFrame);

                                //send all data
                                for (int i = 0; i < DataBytesToSendWithPadding.Length; i += 8)
                                {
                                    LastSentFrame.Data = new byte[8];
                                    Array.Copy(DataBytesToSendWithPadding, i, LastSentFrame.Data, 0, 8);
                                    serialCAN.SendCAN(LastSentFrame);
                                }
                                Console.WriteLine("Data sent.");
                            }
                        }
                        else if (e.CANMessage.Data.ByteArrayToHexString().Substring(6) == "0000000000" && int.TryParse(Encoding.ASCII.GetString(e.CANMessage.Data).Trim('\0'), out requestedBlockIndex))
                        {
                            FileUpdateRunning = true;
                            if (!string.IsNullOrEmpty(RequestedConfigNameData))
                            {
                                Console.WriteLine("Request file {0} block {1}", RequestedConfigNameData, requestedBlockIndex);

                                byte[] bytesToSend = DataBytesToSendWithPadding.Skip(requestedBlockIndex * CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize).Take(CurrentUpdateFiles[CurrentUpdateFileIndex].blockSize).ToArray();

                                byte[] fileSizeBytes = BitConverter.GetBytes(bytesToSend.Length).ToBigEndian();

                                //Prepare messages for later sending (FFFFFF... message)
                                AckconfigFileRequestFrame = e.CANMessage.Clone();
                                AckconfigFileRequestFrame.Id = BuildCANId(CANMaerklinCommand.ConfigDataQueryRsp, CS2AddressHash);
                                serialCAN.SendCAN(AckconfigFileRequestFrame);

                                Array.Copy(fileSizeBytes, LastSentFrame.Data, 4);
                                Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.NonZero1);
                                LastSentFrame.Id = BuildCANId(CANMaerklinCommand.ConfigDataStream, e.CANMessage.Id & 0x00FFFF);
                                var crc16 = crc16Ccitt.ComputeChecksumBytes(bytesToSend).ToBigEndian();
                                LastSentFrame.Data[4] = crc16[0];
                                LastSentFrame.Data[5] = crc16[1];
                                Console.WriteLine("block checksum 0x{0}", crc16.ByteArrayToHexString());
                                Thread.Sleep(100);
                                serialCAN.SendCAN(LastSentFrame);

                                //send block
                                LastSentFrame.Id = BuildCANId(CANMaerklinCommand.ConfigDataStream, e.CANMessage.Id & 0x00FFFF);
                                for (int i = 0; i < bytesToSend.Length; i += 8)
                                {
                                    LastSentFrame.Data = new byte[8];
                                    Array.Copy(bytesToSend, i, LastSentFrame.Data, 0, 8);
                                    serialCAN.SendCAN(LastSentFrame);
                                }
                            }
                        }
                        else
                        {
                            RequestedConfigNameData = Encoding.ASCII.GetString(e.CANMessage.Data).Trim('\0');
                            CurrentUpdateFileIndex = Array.FindIndex(ms2_update_data, x => x.versionName == RequestedConfigNameData);
                            if (Array.Exists(ms2_update_data, x => x.versionName == RequestedConfigNameData))
                            {
                                //CS2 version string request
                                Console.WriteLine("Request .CS2 fileinfo {0}", RequestedConfigNameData);
                                CurrentUpdateFileIndex = Array.FindIndex(ms2_update_data, x => x.versionName == RequestedConfigNameData);
                                LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.ConfigDataStream, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

                                //Prepare messages for later sending (FFFFFF... message)
                                AckconfigFileRequestFrame = e.CANMessage.Clone();
                                AckconfigFileRequestFrame.Id = BuildCANId(CANMaerklinCommand.ConfigDataQueryRsp, CS2AddressHash);
                            }
                            else if (Array.Exists(ms2_update_data, x => x.name == RequestedConfigNameData))
                            {
                                //File data request
                                Console.WriteLine("Request file {0}", RequestedConfigNameData);
                                CurrentUpdateFileIndex = Array.FindIndex(ms2_update_data, x => x.name == RequestedConfigNameData);
                                ReadFirmwareFile(CurrentUpdateFiles[CurrentUpdateFileIndex], false);
                                LastSentFrame = new CANMessage(BuildCANId(CANMaerklinCommand.ConfigDataStream, CS2AddressHash), new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

                                AckconfigFileRequestFrame = e.CANMessage.Clone();
                                AckconfigFileRequestFrame.Id = BuildCANId(CANMaerklinCommand.ConfigDataStreamRsp, CS2AddressHash);
                            }
                            else
                            {
                                Console.WriteLine("Couldn't find requested file {0}", RequestedConfigNameData);
                            }
                        }
                        break;

                    default:
                        break;
                }
            }
        }

        private void SendInitialBlockId(CANMessage canMessage)
        {
            /* prepare test frame for later use */
            Console.WriteLine("Prepare test frame.");
            CheckFrame = canMessage.Clone();
            CheckFrame.Id = BuildCANId(CANMaerklinCommand.BootloaderRsp, canMessage.Id & 0x00FFFF);
            CheckFrame.Data = new byte[] { canMessage.Data[0], canMessage.Data[1], canMessage.Data[2], canMessage.Data[3], 0x88 };
            CheckFrameNack = CheckFrame.Clone();
            LastBinBlock = TotalBlocksToSend;
            Thread.Sleep(100);
            SendNextBlockId((byte)(LastBinBlock + CurrentUpdateFiles[CurrentUpdateFileIndex].blockOffset));
        }
    }
}