using System;
using System.Linq;

namespace SerialCAN
{
    public class CANMessage
    {
        public UInt32 Id { get; set; }
        public byte[] Data { get; set; }
        //True when CAN message is extended type (29bit)
        public bool IsExtended { get; set; }

        //True when CAN message is of RTR (Remote Transmission Request) type
        public bool IsRTR { get; set; }

        public CANMessage()
        { }

        public CANMessage(UInt32 id, byte[] data, bool isExtended = true, bool isRTR = false)
            : this()
        {
            this.Id = id;
            this.Data = data;
            this.IsExtended = isExtended;
            this.IsRTR = isRTR;
        }

        public override string ToString()
        {
            return string.Format("Id:0x{0:X04} Data:{1} Extended:{2} RTR:{3}", Id, Data.ByteArrayToHexString(), IsExtended, IsRTR);
        }

        public CANMessage Clone()
        {
            var newMessage = new CANMessage(this.Id, this.Data.ToArray(), this.IsExtended, this.IsRTR);
            return newMessage;
        }
    }
}
