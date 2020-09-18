using System;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SerialCAN
{
    /// <summary>
    /// Simple and incomplete wrapper class for CAN2Serial protocol (Lawicel)
    /// </summary>
    public class SerialCAN
    {
        public event EventHandler<CANMessageReceivedEventArgs> CANMessageReceived;

        private SerialPort serialPort = null;
        private string pendingData = string.Empty;

        private AutoResetEvent AckReceivedAutoResetEvent = new AutoResetEvent(false);
        bool NakReceived = false;

        private bool Verbose = false;
        public int DelayAfterWriteMilliseconds { get; set; }

        public SerialCAN(string portName, int baudRate, bool verbose)
        {
            this.Verbose = verbose;
            this.serialPort = new SerialPort(portName, baudRate);
            serialPort.Open();
            serialPort.ReceivedBytesThreshold = 1;
            serialPort.DtrEnable = true;
            serialPort.RtsEnable = true;
            serialPort.Handshake = Handshake.None;
            serialPort.ReadTimeout = 1000;
            serialPort.DataReceived += SerialPort_DataReceived;
            //We're patient by default - it looks like my cheap SLCAN adapter (or the target CAN device) doesn't like to hurry...
            DelayAfterWriteMilliseconds = 5;
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            ReceiveData();
        }

        private void ReadLoop(Object stateInfo)
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                try
                {
                    int bytesRead = serialPort.Read(buffer, 0, buffer.Length);
                    //Replace stupid control characters: '\a' (BEL) will get '=\r' with so we can easily parse that ugly stuff
                    pendingData += Encoding.ASCII.GetString(buffer, 0, bytesRead).Replace("\a", "=\r").Replace('\r', '%');

                    string[] lines = Regex.Split(pendingData, "(%)");
                    int removeLength = 0;
                    foreach (string line in lines)
                    {
                        if (line.Length > 0)
                        {
                            ProcessReceivedDataLine(line);
                            //remove processed data
                            removeLength += line.Length;
                        }
                    }

                    pendingData = pendingData.Remove(0, removeLength);
                }
                catch (TimeoutException) { }
            }
        }

        public void ConfigureCANBitrate(CANBitrate canBitrate)
        {
            switch (canBitrate)
            {
                case CANBitrate.Kbit10:
                    SendData("S0");
                    break;
                case CANBitrate.Kbit20:
                    SendData("S1");
                    break;
                case CANBitrate.Kbit50:
                    SendData("S2");
                    break;
                case CANBitrate.Kbit100:
                    SendData("S3");
                    break;
                case CANBitrate.Kbit125:
                    SendData("S4");
                    break;
                case CANBitrate.Kbit250:
                    SendData("S5");
                    break;
                case CANBitrate.Kbit500:
                    SendData("S6");
                    break;
                case CANBitrate.Kbit800:
                    SendData("S7");
                    break;
                case CANBitrate.Kbit1000:
                    SendData("S8");
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public void OpenCAN(bool clean)
        {
            if (clean)
            {
                //Close first
                SendData("C");
                //Send some empty CR's to get a defined startup state
                SendData(string.Empty);
                SendData(string.Empty);
                SendData(string.Empty);
                SendData(string.Empty);
                Thread.Sleep(500);
            }
            
            //Enable auto Polling
            SendData("X1");
            //Open CAN
            SendData("O");
            Thread.Sleep(200);
        }

        public void SendCAN(CANMessage canMessage)
        {
            if (Verbose)
            {
                Console.WriteLine("<-- {0}", canMessage.ToString());
            }

            StringBuilder serialCanMessage = new StringBuilder();
            if (canMessage.IsExtended)
            {
                serialCanMessage.Append(canMessage.IsRTR ? "R" : "T");
                serialCanMessage.Append(canMessage.Id.ToString("X08"));
            }
            else
            {
                serialCanMessage.Append(canMessage.IsRTR ? "r" : "t");
                serialCanMessage.Append(canMessage.Id.ToString("X03"));
            }
            if (canMessage.Data != null && canMessage.Data.Length > 0)
            {
                serialCanMessage.Append(canMessage.Data.Length.ToString("X"));
            }
            else
            {
                serialCanMessage.Append("0");
            }
            if (!canMessage.IsRTR)
            {
                serialCanMessage.Append(canMessage.Data.ByteArrayToHexString());
            }

            SendData(serialCanMessage.ToString());

            Thread.Sleep(DelayAfterWriteMilliseconds);
        }

        public void CloseCAN()
        {
            SendData("C");
        }

        private void SendData(string data)
        {
            serialPort.Write(data);
            serialPort.Write("\r");
            serialPort.BaseStream.Flush();
        }


        private void ReceiveData()
        {
                //Replace stupid control characters: '\a' (BEL) will get '=\r' with so we can easily parse that ugly stuff
                var serialData = serialPort.ReadExisting().Replace("\a", "=\r").Replace('\r', '%');
                pendingData += serialData;

                string[] lines = Regex.Split(pendingData, "(%)");
                int removeLength = 0;
                foreach(string line in lines)
                {
                    if (line.Length > 0)
                    {
                        ProcessReceivedDataLine(line);
                        //remove processed data
                        removeLength += line.Length;
                    }
                }

                pendingData = pendingData.Remove(0, removeLength);
        }

        private void ProcessReceivedDataLine(string dataLine)
        {
            if (dataLine == "%" || dataLine.ToUpper() == "Z")
            {
                NakReceived = false;
                AckReceivedAutoResetEvent.Set();
            }
            else if (dataLine == "=")
            { 
                NakReceived = true;
                AckReceivedAutoResetEvent.Set();
            }
            else
            {
                //process data line
                switch (dataLine[0])
                {
                    case 'T':
                    case 't':
                    case 'r':
                    case 'R':
                        ParseCANMessage(dataLine);
                        break;
    
                    default:
                        //Unknown - ignore
                        break;
                }
            }
        }

        private void ParseCANMessage(string dataLine)
        {
            if (string.IsNullOrEmpty(dataLine))
            {
                throw new ArgumentException();
            }
            switch (dataLine[0])
            {
                case 'T':
                case 'R':
                    //Extended frame
                    CANMessage msg = new CANMessage();
                    if (dataLine.Length >= 10)
                    {
                        //parse address (8 HEX characters)
                        msg.IsExtended = true;
                        msg.IsRTR = dataLine[0] == 'R';
                        msg.Id = UInt32.Parse(dataLine.Substring(1, 8), System.Globalization.NumberStyles.HexNumber);
                        int dataLength = UInt16.Parse(dataLine.Substring(9, 1), System.Globalization.NumberStyles.HexNumber);
                        if (!msg.IsRTR && dataLength > 0)
                        {
                            //parse data
                            if (dataLine.Length == 10 + dataLength * 2)
                            {
                                msg.Data = dataLine.Substring(10, dataLength*2).HexStringToByteArray();
                            }
                            else
                            {
                                //expected more or less data than present
                                throw new FormatException();
                            }
                        }
                        else
                        {
                            msg.Data = new byte[0];
                        }
                        
                        if (Verbose)
                        {
                            Console.WriteLine("--> {0}", msg.ToString());
                        }
                        
                        CANMessageReceived?.Invoke(this, new CANMessageReceivedEventArgs() { CANMessage = msg });
                    }
                    else
                    {
                        //Ignore illegal format
                    }
                    break;
                
                case 't':
                case 'r':
                    //Basic frame
                    msg = new CANMessage();
                    if (dataLine.Length >= 5)
                    {
                        //parse address (3 HEX characters)
                        msg.IsExtended = false;
                        msg.IsRTR = dataLine[0] == 'r';
                        msg.Id = UInt32.Parse(dataLine.Substring(1, 3), System.Globalization.NumberStyles.HexNumber);
                        int dataLength = UInt16.Parse(dataLine.Substring(4, 1), System.Globalization.NumberStyles.HexNumber);
                        if (!msg.IsRTR && dataLength > 0)
                        {
                            //parse data
                            if (dataLine.Length == 5 + dataLength * 2)
                            {
                                msg.Data = dataLine.Substring(5, dataLength * 2).HexStringToByteArray();
                            }
                            else
                            {
                                //expected more or less data than present
                                throw new FormatException();
                            }
                        }
                        else
                        {
                            msg.Data = new byte[0];
                        }
                    }
                    else
                    {
                        throw new FormatException();
                    }
                    CANMessageReceived?.Invoke(this, new CANMessageReceivedEventArgs() { CANMessage = msg });
                    break;


                default:
                    throw new ArgumentException();
            }
        }

        private void WaitForOk(int timeoutMilliseconds)
        {
            bool success = AckReceivedAutoResetEvent.WaitOne(timeoutMilliseconds);
            if(!success || NakReceived)
            {
                throw new TimeoutException("No ACK received!");
            }
        }
    }
}
