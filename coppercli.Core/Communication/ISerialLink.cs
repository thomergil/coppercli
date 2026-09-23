#nullable enable

using System.IO.Ports;

namespace coppercli.Core.Communication
{
    /// <summary>
    /// The serial port as <see cref="SerialProxy"/> uses it, so a test can stand in for the
    /// hardware and read what reached it.
    /// </summary>
    internal interface ISerialLink : IDisposable
    {
        bool IsOpen { get; }

        int BytesToRead { get; }

        Stream BaseStream { get; }

        int Read(byte[] buffer, int offset, int count);

        void Write(byte[] buffer, int offset, int count);

        void Close();
    }

    /// <summary>A real serial port; SerialPort already has every member the proxy uses.</summary>
    internal sealed class SerialPortLink : SerialPort, ISerialLink
    {
        public SerialPortLink(string portName, int baudRate)
            : base(portName, baudRate)
        {
        }
    }
}
