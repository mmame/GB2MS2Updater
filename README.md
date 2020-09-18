# GB2MS2Updater
## Maerklin Gleisbox 2 and Mobile Station 2 Updater

### Description
Allows to update a Märklin Gleisbox 2 or Mobile Station 2 by using a (i.e. USB) SerialCAN dongle connected to a computer
Heavily based on the work found here: [https://github.com/GBert](https://github.com/GBert/railroad/blob/master/can2udp/src/gb2-update.c) 
The application is written in C# / .net Core and has been tested on Windows - Other OS's with .net Core support should work, too.

### Required Hardware
You need a SerialCan/SLCAN compatible dongle. I used [that one](https://www.electrodragon.com/product/can-usb-debugger-board/)

### Usage
Märklin Firmware Updater for the MS2 and GB2 V1.0.0.0

    Usage: GB2MS2Updater -p <ComPort> -f <FirmwarePath> -d <DeviceType>

         -c <ComPort>           Name of the serial port where the SerialCAN adapter is present
         -p <FirmwarePath>      File path where the firmware update files are located
         -d <DeviceType>        DeviceType which has to be updated. Valid values: 'MS2' or 'GB2'
         -f                     Force update even if device has already the same version
         -v                     Verbose output

    Example: GB2MS2Updater -c COM5 -d MS2 -p .\cs3update_v2.10.btrfs\usr\local\cs3\update

### Credits: 
* Gerd (see link above)
* Karsten from Stummiforum
