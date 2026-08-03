using Ares.Toolkit.Serial;
using Ares.Toolkit.Serial.Commands;
using System.IO.Ports;

namespace SerialStreamingSensor.Connection
{
    public class SerialStreamingConnection : AresHardwareConnection, ISerialSteramingConnection
    {
        public SerialStreamingConnection(SerialPortConnectionInfo connectionInfo, string portName, SerialConnectionOptions? connectionOptions = null) : base(connectionInfo, portName, connectionOptions)
        {
        }

        public SerialStreamingConnection(string portName, int baudRate = 115200, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One, SerialConnectionOptions? options = null) : base(portName, baudRate, parity, dataBits, stopBits, options)
        {
            
        }

    }
}
