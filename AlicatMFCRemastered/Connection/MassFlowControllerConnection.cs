using Ares.Toolkit.Serial;
using System.Collections.ObjectModel;
using System.IO.Ports;

namespace AlicatMFCRemastered;

public class MassFlowControllerConnection : AresHardwareConnection, IMfcConnection
{
    const string AlicatAsciiProtocol = "Alicat ASCII Protocol";
    private readonly List<char> _unusedIds;

  public MassFlowControllerConnection(string portName) : base(new SerialPortConnectionInfo(19200, Parity.None, 8, StopBits.One,AlicatAsciiProtocol), portName,
    new SerialConnectionOptions
    {
      SendBuffer = TimeSpan.FromMilliseconds(50),
      SendTimeout = TimeSpan.FromSeconds(2)
    }
  )
  {
    _unusedIds = new List<char>(Enumerable.Range('A', 25).Select(id => (char)id).ToArray());
    UnusedIds = new ReadOnlyCollection<char>(_unusedIds);
    AttemptOpen();
  }

// Device Ids are required to be unique on a given bus. But Alicats on diferent buses could have the same ids
// Current pattern is overly restrictive, consider revision
  public IEnumerable<char> UnusedIds { get; }

  public bool ReserveId(char id)
  {
    if (!UnusedIds.Contains(id))
      return false;

    _unusedIds.Remove(id);
    _unusedIds.Sort((c, c1) => c);
    return true;
  }

  public void ReleaseId(char id)
  {
    if (UnusedIds.Contains(id))
      return;

    _unusedIds.Add(id);
    _unusedIds.Sort((c, c1) => c);
  }
}
