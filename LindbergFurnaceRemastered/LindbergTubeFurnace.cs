using Ares.Datamodel;
using Ares.Datamodel.Device;
using Ares.Datamodel.Extensions;
using Ares.Datamodel.Factories;
using Ares.Device;
using LindbergFurnaceRemastered.Commands;
using LindbergFurnaceRemastered.Commands.Requests;
using LindbergFurnaceRemastered.Connection;
using LindbergFurnaceRemastered.Simulation;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;

namespace LindbergFurnaceRemastered;

public class LindbergTubeFurnace : AresDevice
{
  private readonly BehaviorSubject<AresStruct> _stateSubject = new(new AresStruct());
  private CancellationTokenSource _stateGetterLoopTokenSource = new();
  private CompositeDisposable _stateWatchers = new();
  private Task _stateUpdater = Task.CompletedTask;
  private readonly ILindbergFurnaceConnection _serialConnection;
  private readonly ILogger _logger;
  
  public int AssumedAddress { get; private set; }
  public double CurrentTemperature { get; private set; }
  public double Setpoint { get; private set; }

  public LindbergTubeFurnace(DeviceConnectionInfo connectionInfo, ILogger logger) : base(connectionInfo)
  {
    StateStream = _stateSubject.AsObservable();
    var serialInfo = connectionInfo.SerialConnectionInfo;
    AssumedAddress = serialInfo.HasSerialId ? int.Parse(serialInfo.SerialId) : 1;
    _logger = logger;

    if(connectionInfo.Simulated)
    {
      var temp = new SimLindbergFurnaceConnection(serialInfo.PortName);
      temp.ReserveAddress(AssumedAddress);
      _serialConnection = temp;
    }
    else
    {
      _serialConnection = new LindbergFurnaceConnection(serialInfo.PortName);
    }

    StateSchema = AresSchemaBuilder.Empty()
      .AddEntry("Id", AresSchemaBuilder.StringEntry().Build())
      .AddEntry("Name", AresSchemaBuilder.StringEntry().AsOptional().Build())
      .AddEntry("Address", AresSchemaBuilder.NumberEntry().Build())
      .AddEntry("Current Temperature", AresSchemaBuilder.NumberEntry().Build())
      .AddEntry("Setpoint", AresSchemaBuilder.NumberEntry().Build())
      .Build();
  }

  public override IObservable<AresStruct> StateStream { get; }

  public override async Task<bool> Activate(CancellationToken ct)
  {
    bool activated = false;
    try
    {
      await Initialize();
      activated = true;
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Active, Message = $"Furnace {Name} is active!" };
    }
    catch(Exception e)
    {
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Failed to initialize: {e.Message}" };
    }

    return activated;
  }

  private async Task Initialize()
  {
    await StopUpdateLoop();

    _stateSubject.OnNext(AresStateBuilder.Create()
      .Add("Address", AssumedAddress)
      .Add("Name", Name)
      .Build());

    await StartUpdateLoop(TimeSpan.FromMilliseconds(1000));
  }

  public async Task StartUpdateLoop(TimeSpan interval)
  {
    await StopUpdateLoop();
    _stateGetterLoopTokenSource = new CancellationTokenSource();
    _stateUpdater = Task.Factory.StartNew(async _ =>
    {
      Thread.CurrentThread.Name = $"Furnace {AssumedAddress} State Update Loop Thread";
      try
      {
        while(!_stateGetterLoopTokenSource.IsCancellationRequested)
        {
          try
          {
            await UpdateState();
          }
          catch(Exception)
          {
            // Log error or update status if needed
          }
          await Task.Delay(interval);
        }
      }
      catch(ObjectDisposedException) { }
      catch(Exception e)
      {
        Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"{e.Message}" };
      }
    }, _stateGetterLoopTokenSource.Token);
  }

  private async Task StopUpdateLoop()
  {
    _stateGetterLoopTokenSource.Cancel();
    await _stateUpdater;
  }

  private async Task UpdateState()
  {
    var pv = await GetCurrentTemperatureInternal();
    var sp = await GetSetpointInternal();

    CurrentTemperature = pv;
    Setpoint = sp;

    var next = AresStateBuilder.Create()
      .Add("Current Temperature", CurrentTemperature)
      .Add("Setpoint", Setpoint)
      .Add("Address", AssumedAddress)
      .Add("Id", UniqueId)
      .Build();

    _stateSubject.OnNext(next);
  }

  private async Task<double> GetSetpointInternal()
  {
    var request = new ReadMultipleRegistersRequest(AssumedAddress, Register.SP1, 1);
    var response = await _serialConnection.Send(request);
    var setpointData = response.RegisterContents.First();
    var setpointAsciiHex = setpointData.Select(b => (char)b).ToArray();
    var setpointInt = int.Parse(new string(setpointAsciiHex), NumberStyles.HexNumber);
    return setpointInt;
  }

  private async Task<double> GetCurrentTemperatureInternal()
  {
    var request = new ReadMultipleRegistersRequest(AssumedAddress, Register.PV, 1);
    var response = await _serialConnection.Send(request);
    var temperatureData = response.RegisterContents.First();
    var temperatureAsciiHex = temperatureData.Select(b => (char)b).ToArray();
    var temperatureInt = int.Parse(new string(temperatureAsciiHex), NumberStyles.HexNumber);
    return temperatureInt;
  }

  public async Task SetSetpointInternal(double degreesCelsius)
  {
    var val = (int)degreesCelsius;
    var setpointWrite = new RegisterReadWrite { Register = Register.SP1, UpperDigit = (byte)(val >> 8), LowerDigit = (byte)val };
    var request = new WriteMultipleRegistersRequest(AssumedAddress, setpointWrite);
    await _serialConnection.Send(request);
  }

  public override async Task EnterSafeMode(CancellationToken ct)
  {
    await SetSetpointInternal(25.0);
  }

  public override async Task<CommandResult> ExecuteCommand(string command, List<DeviceCommandArgument> arguments, CancellationToken token)
  {
    if(!Enum.TryParse<TubeFurnaceCommand>(command, out var deviceCommandEnum))
    {
      return new CommandResult { Success = false, Error = $"Invalid command: '{command}'" };
    }

    var result = new CommandResult { Success = true };

    AresValue? GetArg(TubeFurnaceParameter param) =>
        arguments.FirstOrDefault(a => a.ArgName == param.ToString())?.ArgValue;

    try
    {
      switch(deviceCommandEnum)
      {
        case TubeFurnaceCommand.GetSetpoint:
          result.Result = AresValueHelper.CreateNumber(await GetSetpointInternal());
          break;

        case TubeFurnaceCommand.SetSetpoint:
          var param = GetArg(TubeFurnaceParameter.Setpoint);
          if(param is null || !AresValueHelper.IsNumericType(param))
            return ArgumentError("SetSetpoint", "Setpoint", "number");

          var found = param.TryGetNumericValue(out var setpoint);
          await SetSetpointInternal(setpoint);
          break;

        case TubeFurnaceCommand.GetCurrentTemperature:
          result.Result = AresValueHelper.CreateNumber(await GetCurrentTemperatureInternal());
          break;

        case TubeFurnaceCommand.SetAndWaitForSetpoint:
          if(GetArg(TubeFurnaceParameter.Setpoint) is not { HasNumberValue: true, NumberValue: var targetSp })
            return ArgumentError("SetAndWaitForSetpoint", "Setpoint", "number");
          if(GetArg(TubeFurnaceParameter.TemperatureDelta) is not { HasNumberValue: true, NumberValue: var delta })
            return ArgumentError("SetAndWaitForSetpoint", "TemperatureDelta", "number");
          if(GetArg(TubeFurnaceParameter.Timeout) is not { HasNumberValue: true, NumberValue: var timeout })
            return ArgumentError("SetAndWaitForSetpoint", "Timeout", "number");

          await SetAndWaitForSetpointInternal(targetSp, delta, timeout, token);
          break;

        default:
          return new CommandResult { Success = false, Error = $"Command '{deviceCommandEnum}' is defined but execution logic is missing." };
      }
    }
    catch(Exception ex)
    {
      result.Success = false;
      result.Error = ex.Message;
    }

    return result;
  }

  private async Task SetAndWaitForSetpointInternal(double targetTemperature, double delta, double timeout, CancellationToken ct)
  {
    await SetSetpointInternal(targetTemperature);

    var task = StateStream
      .Where(state => state.Fields.TryGetValue("Current Temperature", out var val) && val.HasNumberValue && Math.Abs(targetTemperature - val.NumberValue) <= delta)
      .FirstAsync()
      .ToTask(ct);

    if(timeout == -1)
    {
      await task;
    }
    else
    {
      var timespan = TimeSpan.FromSeconds(timeout);
      await task.WaitAsync(timespan, ct);
    }
  }

  private static CommandResult ArgumentError(string commandName, string paramName, string expectedType)
  {
    return new CommandResult { Success = false, Error = $"The {commandName} command requires a valid {expectedType} for '{paramName}'." };
  }

  protected override Task<List<DeviceCommandDescriptor>> BuildCommandDescriptorsAsync()
  {
    var descriptors = new List<DeviceCommandDescriptor>
    {
      new() 
      { 
        Name = TubeFurnaceCommand.GetSetpoint.ToString(), 
        Description = "Gets the current setpoint from the furnace.",
        OutputSchema = AresSchemaBuilder.FloatEntry().WithDescription("Current Setpoint of the Furnace").Build()
      },
      new() 
      { 
        Name = TubeFurnaceCommand.SetSetpoint.ToString(), 
        Description = "Sets a new target setpoint for the furnace.",
        InputSchema = AresSchemaBuilder.Empty().AddEntry(TubeFurnaceParameter.Setpoint.ToString(), AresSchemaBuilder.NumberEntry().Build()).Build()
      },
      new() 
      { 
        Name = TubeFurnaceCommand.GetCurrentTemperature.ToString(), 
        Description = "Gets the current temperature from the furnace.",
        OutputSchema = AresSchemaBuilder.FloatEntry().WithDescription("Current Temperature of the Furnace").Build()
      },
      new() 
      { 
        Name = TubeFurnaceCommand.SetAndWaitForSetpoint.ToString(), 
        Description = "Sets a new setpoint and waits for the temperature to be within the given delta.",
        InputSchema = AresSchemaBuilder.Empty()
          .AddEntry(TubeFurnaceParameter.Setpoint.ToString(), AresSchemaBuilder.NumberEntry().Build())
          .AddEntry(TubeFurnaceParameter.TemperatureDelta.ToString(), AresSchemaBuilder.NumberEntry().Build())
          .AddEntry(TubeFurnaceParameter.Timeout.ToString(), AresSchemaBuilder.NumberEntry().Build())
          .Build() 
      }
    };
    return Task.FromResult(descriptors);
  }

  public override Task<AresStruct> GetState() => Task.FromResult(_stateSubject.Value);

  public override async Task UpdateSettings(AresStruct settings)
  {
    // No specific settings for now, but could add things like polling interval
    await Task.CompletedTask;
  }

  public override async Task<AresStruct> GetSettings()
  {
    return new AresStruct();
  }

  public async ValueTask DisposeAsync()
  {
    await StopUpdateLoop();
    _stateGetterLoopTokenSource.Dispose();
    _stateSubject.OnCompleted();
  }
}
