using System;

namespace SerialCAN
{
    public class CANMessageReceivedEventArgs : EventArgs
    {
        public CANMessage CANMessage { get; set; }
    }
}
