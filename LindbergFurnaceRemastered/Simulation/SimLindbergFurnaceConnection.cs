using Ares.Toolkit.Serial;
using Ares.Toolkit.Serial.Simulation;
using LindbergFurnaceRemastered.Commands;
using LindbergFurnaceRemastered.Connection;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace LindbergFurnaceRemastered.Simulation;

public class SimLindbergFurnaceConnection : AresSerialSimConnection, ILindbergFurnaceConnection
{
  private readonly List<int> _unusedAddresses;
  private readonly ConcurrentDictionary<int, FurnaceState> _furnaces = new();
  private readonly Random _random = new();
  private readonly object _syncLock = new();

  // Thermal Simulation Parameters
  public double RampUpRatePerSec { get; set; } = 3.0;   // e.g. 3.0°C/sec (~180°C/min)
  public double RampDownRatePerSec { get; set; } = 1.5; // Natural cooling rate
  public double FluctuationDelta { get; set; } = 0.5;   // +/- 0.5°C jitter at steady state

  public SimLindbergFurnaceConnection(string portName) : base(new SerialPortConnectionInfo(
      9600,
      System.IO.Ports.Parity.Even,
      7,
      System.IO.Ports.StopBits.One), portName)
  {
    _unusedAddresses = [.. Enumerable.Range(1, 247).ToArray()];
    UnusedAddresses = new ReadOnlyCollection<int>(_unusedAddresses);
  }

  public IEnumerable<int> UnusedAddresses { get; }

  public bool ReserveAddress(int address)
  {
    if(!_unusedAddresses.Contains(address))
      return false;

    _unusedAddresses.Remove(address);
    _unusedAddresses.Sort();
    return true;
  }

  public void ReleaseAddress(int address)
  {
    if(_unusedAddresses.Contains(address))
      return;

    _unusedAddresses.Add(address);
    _unusedAddresses.Sort();
  }

  public override void SendInternally(byte[] bytes)
  {
    var requestStr = Encoding.UTF8.GetString(bytes).Trim();
    if(requestStr.Length < 5 || !requestStr.StartsWith(":"))
      return;

    // Strip leading ':' and trailing whitespace
    var payload = requestStr.TrimStart(':');
    if(payload.Length < 4)
      return;

    // Extract Address (2 hex chars) and Function Code (2 hex chars)
    var addrHex = payload.Substring(0, 2);
    var fcStr = payload.Substring(2, 2);

    if(!int.TryParse(addrHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address))
    {
      address = 1;
    }

    string responseStr;

    lock(_syncLock)
    {
      var furnace = _furnaces.GetOrAdd(address, _ => new FurnaceState(ambientTemp: 25.0));

      // FC 06: Write Single Register (e.g. Setpoint)
      if(fcStr == "06" && payload.Length >= 12)
      {
        var regAddrHex = payload.Substring(4, 4);
        var valHex = payload.Substring(8, 4);

        if(ushort.TryParse(valHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort newSetpoint))
        {
          furnace.SetSetpoint(newSetpoint, RampUpRatePerSec, RampDownRatePerSec);
        }

        // Echo request frame back per Modbus FC 06 standard
        var responseBody = $"{addrHex}{fcStr}{regAddrHex}{valHex}";
        var lrc = $"{TubeFurnaceCommandHelper.Lrc(responseBody.Select(c => (byte)c)):X2}";
        responseStr = $":{responseBody}{lrc}\r\n";
      }
      // FC 16 (0x10): Write Multiple Registers
      else if((fcStr == "10" || fcStr == "16") && payload.Length >= 14)
      {
        var regAddrHex = payload.Substring(4, 4);
        var countHex = payload.Substring(8, 4);
        var valHex = payload.Substring(14, 4);

        if(ushort.TryParse(valHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort newSetpoint))
        {
          furnace.SetSetpoint(newSetpoint, RampUpRatePerSec, RampDownRatePerSec);
        }

        var responseBody = $"{addrHex}{fcStr}{regAddrHex}{countHex}";
        var lrc = $"{TubeFurnaceCommandHelper.Lrc(responseBody.Select(c => (byte)c)):X2}";
        responseStr = $":{responseBody}{lrc}\r\n";
      }
      // FC 03 / 04 or generic Read: Output current simulated temperature
      else
      {
        int currentTemp = furnace.GetSimulatedTemperature(RampUpRatePerSec, RampDownRatePerSec, FluctuationDelta, _random);
        currentTemp = Math.Clamp(currentTemp, 0, 9999);

        var responseStart = payload.Substring(0, 4);
        var responseBody = $"{responseStart}{4:x2}{currentTemp:X4}";
        var lrc = $"{TubeFurnaceCommandHelper.Lrc(responseBody.Select(c => (byte)c)):X2}";
        responseStr = $":{responseBody}{lrc}\r\n";
      }
    }

    var responseBytes = Encoding.UTF8.GetBytes(responseStr);
    AddDataReceived(responseBytes);
  }

  /// <summary>
  /// Internal thermal state tracking per furnace address.
  /// </summary>
  private class FurnaceState
  {
    public double CurrentTemp { get; private set; }
    public double Setpoint { get; private set; }
    public DateTime LastUpdate { get; private set; }

    public FurnaceState(double ambientTemp)
    {
      CurrentTemp = ambientTemp;
      Setpoint = ambientTemp;
      LastUpdate = DateTime.UtcNow;
    }

    public void SetSetpoint(double setpoint, double rampUpRate, double rampDownRate)
    {
      UpdatePhysics(rampUpRate, rampDownRate);
      Setpoint = setpoint;
    }

    public int GetSimulatedTemperature(double rampUpRate, double rampDownRate, double noiseDelta, Random random)
    {
      UpdatePhysics(rampUpRate, rampDownRate);

      // Add realistic noise jitter (+/- noiseDelta)
      double noise = (random.NextDouble() * 2.0 - 1.0) * noiseDelta;
      double outputTemp = Math.Max(0, CurrentTemp + noise);

      return (int)Math.Round(outputTemp);
    }

    private void UpdatePhysics(double rampUpRate, double rampDownRate)
    {
      var now = DateTime.UtcNow;
      double elapsedSeconds = (now - LastUpdate).TotalSeconds;
      LastUpdate = now;

      if(elapsedSeconds <= 0)
        return;

      if(CurrentTemp < Setpoint)
      {
        CurrentTemp += rampUpRate * elapsedSeconds;
        if(CurrentTemp > Setpoint)
          CurrentTemp = Setpoint;
      }
      else if(CurrentTemp > Setpoint)
      {
        CurrentTemp -= rampDownRate * elapsedSeconds;
        if(CurrentTemp < Setpoint)
          CurrentTemp = Setpoint;
      }
    }
  }
}