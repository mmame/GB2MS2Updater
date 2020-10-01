using System;
using System.IO;
using System.Reflection;

namespace GB2MS2Updater
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                string comPort = null;
                string firmwarePath = null;
                DeviceType deviceType = 0;
                bool forceUpdate = false;
                bool verbose = false;
                bool filesOnly = false;
                int argParseState = 0;
                
                foreach (string arg in args)
                {
                    if (arg.StartsWith("-"))
                    {
                        if (argParseState > 0)
                        {
                            ShowUsage();
                            return;
                        }
                        switch (arg.ToLower())
                        {
                            case "-c":
                                argParseState = 1;
                                break;

                            case "-p":
                                argParseState = 2;
                                break;

                            case "-d":
                                argParseState = 3;
                                break;

                            case "-f":
                                forceUpdate = true;
                                break;

                            case "-filesonly":
                                filesOnly = true;
                                break;

                            case "-v":
                                verbose = true;
                                break;

                            default:
                                ShowUsage();
                                return;
                        }
                    }
                    else
                    {
                        switch (argParseState)
                        {
                            case 0:
                                ShowUsage();
                                return;
                            case 1:
                                comPort = arg;
                                argParseState = 0;
                                break;
                            case 2:
                                firmwarePath = arg;
                                argParseState = 0;
                                break;
                            case 3:
                                deviceType = (DeviceType)Enum.Parse(typeof(DeviceType), arg, true);
                                argParseState = 0;
                                break;
                            default:
                                ShowUsage();
                                return;
                        }
                    }
                }

                if (string.IsNullOrEmpty(comPort) || string.IsNullOrEmpty(firmwarePath) || deviceType == 0)
                {
                    ShowUsage();
                    return;
                }

                DateTime startDateTime = DateTime.Now;

                var updater = new GB2MS2Updater(comPort, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"MaerklinCS2\CS2\update"), deviceType, verbose);
                if (filesOnly)
                {
                    updater.UpdateMS2Files();
                }
                else
                {
                    updater.StartUpdate(forceUpdate);
                }

                Console.WriteLine("{0}s elapsed. Press any key to continue", (DateTime.Now - startDateTime).TotalSeconds);
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: {0}", ex.ToString());
            }
        }

        static void ShowUsage()
        {
            Console.WriteLine("Märklin Firmware Updater for the MS2 and GB2 V{0}\n", Assembly.GetExecutingAssembly().GetName().Version);
            Console.WriteLine("Usage: {0} -p <ComPort> -f <FirmwarePath> -d <DeviceType>\n", Assembly.GetExecutingAssembly().GetName().Name);
            Console.WriteLine();
            Console.WriteLine("         -c <ComPort>           Name of the serial port where the SerialCAN adapter is present");
            Console.WriteLine("         -p <FirmwarePath>      File path where the firmware update files are located");
            Console.WriteLine("         -d <DeviceType>        DeviceType which has to be updated. Valid values: 'MS2' or 'GB2'");
            Console.WriteLine("         -f                     Force update even if device has already the same version");
            Console.WriteLine("         -filesonly             Only update MS2 files");
            Console.WriteLine("         -v                     Verbose output");
            Console.WriteLine();
            Console.WriteLine("Example: {0} -c COM5 -d MS2 -p .\\cs3update_v2.10.btrfs\\usr\\local\\cs3\\update", Assembly.GetExecutingAssembly().GetName().Name);
        }
    }
}
