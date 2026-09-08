using AlicatMFCRemasterd.Commands;
using AlicatMFCRemastered.Commands.Requests;
using AlicatMFCRemastered.Commands.Responses;
using AlicatMFCRemastered.Commands.Responses.Streamed;
using AlicatMFCRemastered.Enums;
using AlicatMFCRemastered.Models;
using AlicatMFCRemastered.Simulation;
using Ares.Datamodel;
using Ares.Datamodel.Device;
using Ares.Datamodel.Extensions;
using Ares.Datamodel.Factories;
using Ares.Device;
using Ares.Toolkit.Serial;
using Microsoft.Extensions.Logging;
using Parsers.AlicatMFCRemastered;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using UnitsNet;
using UnitsNet.Units;

namespace AlicatMFCRemastered;

public class MassFlowController : AresDevice, IMassFlowController
{
    private readonly int _expectedDataFormatEntryCount;
  private readonly BehaviorSubject<AresStruct> _stateSubject = new(new AresStruct());
  private CancellationTokenSource _stateGetterLoopTokenSource = new();
  private CompositeDisposable _stateWatchers = new();
  private Task _stateUpdater = Task.CompletedTask;
  private List<GasInfoEntry> _gases = new();
  private List<ManufacturerInfoEntry> _manufacturerInfo = new();
  private List<DataFrameFormatEntry> _dataFrameFormatEntries = new();
  private LiveDataResponse? _liveData;
  private readonly IMfcConnection _serialConnection;
  private MfcTypeEnum _mfcType;
  private ILogger _logger;
  private MfcSetpointSourceEnum _setpointSource = MfcSetpointSourceEnum.UnknownSource;

  public MassFlowController(DeviceConnectionInfo connectionInfo, ILogger logger) : base(connectionInfo)
  {
    HasValve = connectionInfo.DeviceSettings.Fields["HasValve"]?.BoolValue ?? false;
    _mfcType = connectionInfo.DeviceSettings.Fields["IsBasis"].BoolValue ? MfcTypeEnum.Basis2 : MfcTypeEnum.Normal;
    _logger = logger;
    StateStream = _stateSubject.AsObservable();
    var serialInfo = connectionInfo.SerialConnectionInfo;
    AssumedId = serialInfo.HasSerialId ? serialInfo.SerialId[0] : 'A';
    _logger.LogInformation($"ARES connected a new Alicat MFC to the system named {Name}. The ID of this new Alicat MFC is {AssumedId}");

    if (connectionInfo.Simulated)
    {
      var temp = new SimMassFlowControllerConnection(serialInfo.PortName);
      temp.AddCat(AssumedId, _mfcType);
      _serialConnection = temp;
    }

    else
      _serialConnection = new MassFlowControllerConnection(serialInfo.PortName);

    _stateWatchers = new CompositeDisposable
    {
      _serialConnection.GetTransactionStream<LiveDataResponse>().Select(transaction => transaction.Response).Subscribe(UpdateLiveData)
    };

    _expectedDataFormatEntryCount = _mfcType == MfcTypeEnum.Normal ? 12 : 7;
    
    StateSchema = AresSchemaBuilder.Empty()
    .AddEntry("Id", AresSchemaBuilder.StringEntry().Build())
    .AddEntry("Name", AresSchemaBuilder.StringEntry().Build())
    .AddEntry("HasValve", AresSchemaBuilder.Entry(AresDataType.Boolean).Build())
    .AddEntry("Firmware", AresSchemaBuilder.StringEntry().Build())
    .AddEntry("LiveData", AresSchemaBuilder.Entry(AresDataType.Struct)
        .WithStructSchema(liveData =>
        {
          liveData.Fields.Add("Temperature", AresSchemaBuilder.NumberEntry().Build());
          liveData.Fields.Add("MassFlow", AresSchemaBuilder.NumberEntry().Build());
          liveData.Fields.Add("Setpoint", AresSchemaBuilder.NumberEntry().Build());

          // Note: These are marked as Optional because they are conditional in your StateBuilder
          liveData.Fields.Add("AbsolutePressure", AresSchemaBuilder.NumberEntry().AsOptional().Build());
          liveData.Fields.Add("VolumetricFlow", AresSchemaBuilder.NumberEntry().AsOptional().Build());
          liveData.Fields.Add("ValveDrive", AresSchemaBuilder.NumberEntry().AsOptional().Build());

          // The StatusCodes List (List of Strings)
          liveData.Fields.Add("StatusCodes", AresSchemaBuilder.Entry(AresDataType.List)
              .WithListElementSchema(AresDataType.String)
              .Build());
        })
        .Build())
    .AddEntry("Gases", AresSchemaBuilder.Entry(AresDataType.List)
        .WithListElementSchema(element => {
          element.WithStructSchema(gasStruct => {
            gasStruct.Fields.Add("Gas", AresSchemaBuilder.StringEntry().Build());
            gasStruct.Fields.Add("Index", AresSchemaBuilder.NumberEntry().Build());
            gasStruct.Fields.Add("IsEndMarker", AresSchemaBuilder.Entry(AresDataType.Boolean).Build());
            gasStruct.Fields.Add("Id", AresSchemaBuilder.NumberEntry().Build());
            gasStruct.Fields.Add("RequestId", AresSchemaBuilder.StringEntry().Build());
          });
        })
        .Build())
    .Build();
  }

  public async Task<bool> QueryManufacturerInfo()
  {
    if(_mfcType == MfcTypeEnum.Basis2)
    {
      _logger.LogWarning($"Attemped to query manufacturer information for Alicat MFC, but the Alicat was a Basis and doesn't support that action.");
      return false;
    }

    // for now we just get entry 4 from the manufacturer info as that contains the model number we need to
    // get the flow limit of the device
    var infoIdx = 4;
    var endMarkerReached = false;

    var command = new ManufactureInfoRequest(AssumedId, FirmwareVersion, infoIdx);
    try
    {
      var response = await GetResponseWithRetry<ManufacturerInfoEntry, ManufactureInfoRequest>(command, 5, TimeSpan.FromSeconds(10));
      _manufacturerInfo.Add(response);
      UpdateManufacturerInfo(response);
      UpdatePotentialMaxValue(response);
      endMarkerReached = response.IsEndMarker;
    }
    catch(TimeoutException e)
    {
      Trace.WriteLine($"Timed out while trying to get manufacturer info: {e.Message}");
      endMarkerReached = true;
    }

    return _manufacturerInfo?.Any(info => info.ManufacturerInfoEntryType == ManufacturerInfoEntryTypeEnum.ModelNumber) ?? false;
  }

  /// <summary>
  /// Finds a potential max value from a manufacturer info entry containing the MFC model number
  /// assuming it's something similar to "MC-500SCCM-D" and applies it to a data frame format if found
  /// </summary>
  private void UpdatePotentialMaxValue(ManufacturerInfoEntry entry)
  {
    if(entry.EntryNumber != 4)
      return;

    var dataFrameFormat = _dataFrameFormatEntries?.FirstOrDefault(entry => entry.Field == DataFormatField.Setpoint);
    if(dataFrameFormat is not null)
    {
      if(dataFrameFormat.MaxVal is null)
      {
        var modelNumber = entry.Data.Split('-').Skip(1).FirstOrDefault();
        if(modelNumber is null)
        {
          _logger.LogWarning($"Failed to get max value for MFC {Name} with model number {entry.Data}");
          return;
        }
        var numMatch = Regex.Match(modelNumber, @"\d+");
        var num = numMatch.Success ? numMatch.Value : default;
        var unitMatch = Regex.Match(modelNumber, @"[A-Z]+");
        if(unitMatch.Success)
        {
          var unitFound = MfcUnitParser.Parser.TryParse<StandardVolumeFlowUnit>(unitMatch.Value, out var unit);
          if(!unitFound)
          {
            _logger.LogWarning($"Failed to get max value for MFC {Name} as we couldn't get the value units from model number {entry.Data}");
            return;
          }
          if(!int.TryParse(num, out var numericNum) || numericNum <= 0)
          {
            _logger.LogWarning($"Failed to get max value for MFC {Name} as we couldn't get the numeric max value from model number {entry.Data}");
            return;
          }
          _logger.LogInformation($"Found a potential max value of {numericNum} {unit} for MFC {Name} from model number {entry.Data}");
            var flowVal = StandardVolumeFlow.From(numericNum, unit);
// must be converted to match setpoint units, otherwise may cause issues when calculating newsetpoint
          // dataFrameFormat.MaxVal = flowVal.StandardLitersPerMinute.ToString();
                    dataFrameFormat.MaxVal = flowVal.As((StandardVolumeFlowUnit)dataFrameFormat.Unit).ToString();
                }
      }
    }
  }

  public async Task ChangeHardwareUnitId(char targetId)
  {
    await StopUpdateLoop();
    if(_serialConnection is null)
    {
      var message = $"Alicat MFC {Name} tried to change to an ID of {targetId}, but failed because it's connection was null.";
      _logger.LogError(message);
      return;
    }

    var reservedId = _serialConnection.ReserveId(targetId);

    if(!reservedId)
    {
      var message = $"Alicat MFC tried to switch to use {targetId} as it's ID, but it was in use by another Alicat!";
      _logger.LogError(message);
      return;
    }

    if(_mfcType == MfcTypeEnum.Basis2)
    {
      await ChangeBasisHardwareUnitId(targetId);
    }
    else if(_mfcType == MfcTypeEnum.Normal)
    {
      await ChangeNormalHardwareUnitId(targetId);
    }

  }

  private async Task ChangeNormalHardwareUnitId(char targetId)
  {
    var command = new ChangeIdCommand(AssumedId, targetId, FirmwareVersion);
    GenericLineResponse? result = null;
    try
    {
      result = await _serialConnection.Send(command, TimeSpan.FromSeconds(10), CancellationToken.None, response => response.Id == targetId);
    }
    catch(TimeoutException)
    {
      _logger.LogError("Alicat MFC encountered a timeout exception while trying to change it's ID");
    }

    if(result is null)
    {
      _serialConnection.ReleaseId(targetId);
      _logger.LogError($"Could not get a response trying to change the ID of Alicat MFC {Name} from {AssumedId} to {targetId}");
      return;
    }

    _serialConnection.ReleaseId(AssumedId);
    AssumedId = targetId;
    try
    {
      await Initialize();
    }
    catch(Exception e)
    {
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Failed to initialize: {e.Message}" };
    }
  }

  private async Task ChangeBasisHardwareUnitId(char targetId)
  {
    var command = new BasisChangeIdCommand(AssumedId, targetId, GetFormatEntries(), FirmwareVersion);
    await _serialConnection.Send(command);
    await Task.Delay(500); // we don't get a response back immediately, so we have to assume slight delay

    try
    {
      var liveData = await GetLiveData();
    }
    catch(TimeoutException)
    {
      _serialConnection.ReleaseId(targetId);
      _logger.LogError($"Could not get a response trying to query live data for Alicat MFC {Name}");
      return;
    }

    _serialConnection.ReleaseId(AssumedId);
    AssumedId = targetId;
    try
    {
      await Initialize();
    }
    catch(TimeoutException e)
    {
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Failed to initialize: {e.Message}" };
    }
  }

  public Task CancelValveHold()
  {
    var cancelValveHoldRequest = new CancelValveHoldCommand(AssumedId, GetFormatEntries(), FirmwareVersion);
    return Send(cancelValveHoldRequest);
  }

  public Task ChooseDifferentGas(int gasNumber)
  {
    var chooseDifferentGasCommand = new ChooseDifferentGasCommand(AssumedId, gasNumber, GetFormatEntries(), FirmwareVersion, _mfcType);
    return Send(chooseDifferentGasCommand);
  }

  public async Task SetSetpointSource(MfcSetpointSourceEnum source)
  {
    var request = new SetSetpointSourceCommand(AssumedId, source, ":)");

    await Send(request);
  }

  public async Task<MfcSetpointSourceEnum> GetSetpointSource()
  {
    if(_mfcType != MfcTypeEnum.Basis2)
      return MfcSetpointSourceEnum.UnknownSource;

    var request = new GetSetpointSourceCommand(AssumedId);
    var response = await Send(request);
    return response.Source;
  }

  public double? GetSetpoint()
    => _liveData?.Setpoint?.Value;

  private async Task QueryBasisGasList()
  {
    var request = new BasisQueryGasCommand(AssumedId, ":)");

    var response = await Send(request, TimeSpan.FromSeconds(10));

    foreach(var gasInfo in response.GasInfoEntries)
    {
      UpdateGasInfo(gasInfo);
    }
  }

  // TODO: Implement a more qualified response to the query, the documentation doesn't show the response syntax
  public async Task<bool> QueryGasListInfo()
  {
    var gasIdx = 0;
    var endMarkerReached = false;
    while(!endMarkerReached)
    {
      var command = new QueryGasCommand(AssumedId, FirmwareVersion, _mfcType, gasIdx);
      try
      {
        var response = await GetResponseWithRetry<GasInfoEntry, QueryGasCommand>(command, 5, TimeSpan.FromSeconds(10));
        UpdateGasInfo(response);
        endMarkerReached = response.IsEndMarker;
      }
      catch(TimeoutException e)
      {
        Trace.WriteLine($"Timed out while trying to get gas info entry: {e.Message}");
        endMarkerReached = true;
      }

      gasIdx++;
    }

    return _gases.Count() > 0;
  }

  public async Task QueryFirmwareVersion()
  {
    var request = new MfcFirmwareRequest(AssumedId);
    try
    {
      var response = await Send(request, TimeSpan.FromSeconds(10));
      FirmwareVersion = response.FirmwareVersion;
    }
    catch(OperationCanceledException)
    {
      FirmwareVersion = string.Empty;
    }
    catch(TimeoutException)
    {
      FirmwareVersion = string.Empty;
    }
  }

  public override Task<AresStruct> GetState()
  => Task.FromResult(_stateSubject.Value);


  private void UpdateBasisDataFrames()
  {
    var formatEntries = new DataFrameFormatEntry[] {
      new(AssumedId, 1, DataFormatField.UnitId, "string", null, null, null, "", null),
      new(AssumedId, 2, DataFormatField.Temperature, "s decimal", null, null, null, "", TemperatureUnit.DegreeCelsius),
      new(AssumedId, 3, DataFormatField.Mass, "s decimal", null, null, null, "", StandardVolumeFlowUnit.StandardLiterPerMinute),
      new(AssumedId, 4, DataFormatField.TotalizedMassFlow, "s decimal", null, null, null, "", StandardVolumeFlowUnit.StandardLiterPerMinute),
      new(AssumedId, 5, DataFormatField.Setpoint, "s decimal", null, null, null, "", StandardVolumeFlowUnit.StandardLiterPerMinute),
      new(AssumedId, 6, DataFormatField.ValveDrive, "s decimal", null, null, null, "", null),
      new(AssumedId, 7, DataFormatField.Gas, "string", null, null, null, "", null),
      new(AssumedId, 8, DataFormatField.Status, "string", null, null, "3", "TOV", null),
      new(AssumedId, 8, DataFormatField.Status, "string", null, null, "3", "MOV", null),
      new(AssumedId, 8, DataFormatField.Status, "string", null, null, "3", "OVR", null),
      new(AssumedId, 8, DataFormatField.Status, "string", null, null, "3", "HLD", null),
      new(AssumedId, 8, DataFormatField.Status, "string", null, null, "3", "VTM", null),
    };

    foreach(var entry in formatEntries)
    {
      UpdateDataFrameFormat(entry);
    }
  }

  public async Task<bool> QueryDataFrameFormat()
  {
    var formatIdx = 0;
    var endMarkerReached = false;
    while(!endMarkerReached)
    {
      var command = new DataFormatRequest(AssumedId, FirmwareVersion, formatIdx);
      try
      {
        var response = await GetResponseWithRetry<DataFrameFormatEntry, DataFormatRequest>(command, 5, TimeSpan.FromSeconds(10));
        UpdateDataFrameFormat(response);
        endMarkerReached = response.EntryType == DataFrameFormatEntryType.EndMarker;
      }
      catch(TimeoutException e)
      {
        Trace.WriteLine($"Timed out while trying to get data frame entry: {e.Message}");
        //throw;
        endMarkerReached = true;
      }

      formatIdx++;
    }

    return _dataFrameFormatEntries.Count() >= 7;
  }

  public Task DeleteComposerMix(int mixNumber)
  {
    var deleteMixCommand = new DeleteComposerMixCommand(AssumedId, mixNumber, GetFormatEntries(), FirmwareVersion);
    return Send(deleteMixCommand);
  }

  public Task HoldValvesAtCurrentPosition()
  {
    if(_mfcType == MfcTypeEnum.Normal)
    {
      var holdValvesCommand = new HoldValvesAtCurrentPositionCommand(AssumedId, GetFormatEntries(), FirmwareVersion);
      return Send(holdValvesCommand);
    }
    else if(_mfcType == MfcTypeEnum.Basis2)
    {
      if(_liveData?.ValveDrive is null)
        return Task.CompletedTask;

      var holdValvesCommand = new BasisHoldValvesAtCurrentPositionCommand(AssumedId, FirmwareVersion, GetFormatEntries(), _liveData.ValveDrive.Value);
      return Send(holdValvesCommand);
    }

    return Task.CompletedTask;

  }

  public Task HoldValvesClosed()
  {
    if(_mfcType == MfcTypeEnum.Normal)
    {
      var holdValvesClosedCommand = new HoldValvesClosedCommand(AssumedId, GetFormatEntries(), FirmwareVersion);
      return Send(holdValvesClosedCommand);
    }
    else if(_mfcType == MfcTypeEnum.Basis2)
    {
      var holdValvesClosedCommand = new BasisHoldValvesClosedCommand(AssumedId, GetFormatEntries(), FirmwareVersion);
      return Send(holdValvesClosedCommand);
    }

    return Task.CompletedTask;

  }

  public Task NewComposerMix(MfcGasComposition composerMix)
  {
    var newMixCommand = new NewComposerMixCommand(AssumedId, composerMix, GetFormatEntries(), FirmwareVersion);
    return Send(newMixCommand);
  }

  public async Task NewSetpoint(StandardVolumeFlow setpoint)
  {
    if(_mfcType == MfcTypeEnum.Normal)
    {          
      var newSetpointCommand = new NewSetpointCommand(AssumedId, setpoint, GetFormatEntries(), FirmwareVersion);
            try
      {
        var response = await Send(newSetpointCommand, TimeSpan.FromSeconds(10));
      }
      catch(TimeoutException)
      {
        Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Tried setting setpoint to {setpoint.StandardLitersPerMinute}, but timed out while awaiting response." };
        throw;
      }
    }
    else if(_mfcType == MfcTypeEnum.Basis2)
    {
      var newSetpointCommand = new BasisNewSetpointCommand(AssumedId, setpoint, GetFormatEntries(), FirmwareVersion);
      try
      {
        await Send(newSetpointCommand, TimeSpan.FromSeconds(10));
      }
      catch(TimeoutException)
      {
        Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Tried setting setpoint to {setpoint.StandardLitersPerMinute}, but timed out while awaiting response." };
        throw;
      }
    }
  }

  public Task TareAbsolutePressureWithBarometer()
  {
    // ignore taring on BASIS for now, implement later when/if needed
    if(_mfcType != MfcTypeEnum.Normal)
      return Task.CompletedTask;

    var tarePressureCommand = new TareAbsolutePressureWithBarometerCommand(AssumedId, GetFormatEntries(), FirmwareVersion);
    return Send(tarePressureCommand);
  }

  public Task TareFlow()
  {
    // ignore taring on BASIS for now, implement later when/if needed
    if(_mfcType != MfcTypeEnum.Normal)
      return Task.CompletedTask;

    var tareFlowCommand = new TareFlowCommand(AssumedId, GetFormatEntries(), FirmwareVersion);
    return Send(tareFlowCommand);
  }

  public char AssumedId { get; private set; }

  public override IObservable<AresStruct> StateStream { get; }

  public string FirmwareVersion { get; private set; } = string.Empty;
  public bool HasValve { get; }

  public override async Task<bool> Activate(CancellationToken ct)
  {
    bool activated = false;
    try
    {
      await Initialize();
      activated = true;
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Active, Message = $"MFC {Name} is active!" };
    }
    catch(Exception e)
    {
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Failed to initialize: {e.Message}" };
    }

    return activated;
  }

  public override async Task EnterSafeMode(CancellationToken ct)
  {
    //Set the setpoint to zero, effectively shutting off the MFC.. I think
    await NewSetpoint(StandardVolumeFlow.FromStandardLitersPerMinute(0.0));
    await HoldValvesClosed();
  }

  public async ValueTask DisposeAsync()
  {
    _stateWatchers.Dispose();
    await _stateGetterLoopTokenSource.CancelAsync();
    await _stateUpdater;
    _stateGetterLoopTokenSource.Dispose();
    _stateSubject.OnCompleted();
  }

  private Task<LiveDataResponse> GetLiveData()
  {
    var formatEntries = _dataFrameFormatEntries?.ToArray();
    if(formatEntries is null)
    {
      _logger.LogError($"Failed to retrieve live data, format entries have not been initialized. Format entries need to be acquired first.");
      throw new InvalidOperationException($"Cannot get live data as the format entries have not even been initialized. Need to acquire the format entries first.");
    }

    if(formatEntries.Length < _expectedDataFormatEntryCount)
    {
      _logger.LogError($"ALICAT MFC {Name}: Cannot request live data without knowing format entreis. Number of currently known formats: {formatEntries.Length}, Expected at least {_expectedDataFormatEntryCount}");
      throw new InvalidOperationException($"Cannot request live data without knowing format entries. Number of currently known formats: {formatEntries.Length}, Expected at least {_expectedDataFormatEntryCount}");

    }

    var command = new LiveDataRequest(formatEntries, FirmwareVersion);
    return Send(command, TimeSpan.FromSeconds(10));
  }

  private async Task Initialize()
  {
    if(_serialConnection is null)
    {
      _logger.LogError($"ALICAT MFC {Name}: Initialize was called, but connection was not set!");
      return;
    }

    await StopUpdateLoop();

    _stateSubject.OnNext(AresStateBuilder.Create()
      .Add("Id", AssumedId.ToString())
      .Add("Name", Name)
      .Add("HasValve", HasValve)
      .Add("Firmware", FirmwareVersion)
      .AddList("Gases", Array.Empty<AresValue>(), _ => _)
      .Build());

    if(_mfcType == MfcTypeEnum.Normal)
    {
      await InitNormal();
    }
    else if(_mfcType == MfcTypeEnum.Basis2)
    {
      await InitBasis();
    }

    _ = Start();

    SettingSchema.AddEntry("Selected Gas", AresDataType.String, false, _gases.Select(g => g.Gas));
  }

  private async Task InitNormal()
  {
        _logger.LogInformation($"### ALICAT MFC {Name}: Initializing. ###");

        if (!_serialConnection.IsOpen) _serialConnection.AttemptOpen();
        _logger.LogInformation($"### ALICAT MFC {Name}: Port verified open. ###");

        _logger.LogInformation($"### ALICAT MFC {Name}: Querying data frames. ###");
        var dataFrameQuerySuccess = await QueryDataFrameFormat();
    if(!dataFrameQuerySuccess)
    {
      _logger.LogError($"### ALICAT MFC {Name}: Failed to query the data frames. ###");
      throw new InvalidOperationException("Failed to query the data frames.");
    }


    var importantEntries = Enumerable.Range(1, 7);
    if(!importantEntries.All(entryNum => _dataFrameFormatEntries?.Any(entry => entry.EntryNumber == entryNum) ?? false))
    {
      Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = "Did not receive Data Frame Entries 1-7. Could be missing one, could be missing all." };
      return;
    }
    var gasQuerySuccess = await QueryGasListInfo();
    if(!gasQuerySuccess)
    {
      _logger.LogError($"### ALICAT MFC {Name}: Failed to query the gas list.");
      throw new InvalidOperationException("Failed to query the gas list.");
    }
    await QueryFirmwareVersion();
    var manufacturerInfoQuerySuccess = await QueryManufacturerInfo();
    if(!manufacturerInfoQuerySuccess)
    {
      _logger.LogError($"### ALICAT MFC {Name}: Failed to query the manufacturer info.");
      throw new InvalidOperationException("Failed to query the manufacturer info.");
    }
  }

  private async Task InitBasis()
  {
    _setpointSource = await GetSetpointSource();
    UpdateBasisDataFrames();
    await QueryBasisGasList();
    await QueryFirmwareVersion();
    SettingSchema.AddEntry("Setpoint Source", AresDataType.String, false, Enum.GetNames(typeof(MfcSetpointSourceEnum)));
  }

  public async Task StartUpdateLoop(TimeSpan interval)
  {
    await StopUpdateLoop();
    await Task.Delay(150);
    _stateGetterLoopTokenSource = new CancellationTokenSource();
    _stateUpdater = Task.Factory.StartNew(async _ =>
    {
      Thread.CurrentThread.Name = $"MFC {AssumedId} State Update Loop Thread";
      try
      {
        while(!_stateGetterLoopTokenSource.IsCancellationRequested)
        {
          try
          {
            var liveData = await GetLiveData();
          }
          catch(TimeoutException)
          {
            Status = new DeviceOperationalStatus { OperationalState = OperationalState.Active, Message = $"Get Live Data timed out at {DateTime.Now}" };
          }
          catch(Exception)
          {

          }
          await Task.Delay(interval);
        }
      }
      catch(ObjectDisposedException)
      {
      }
      catch(Exception e)
      {
        Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"{e.Message}" };
      }
    },
      _stateGetterLoopTokenSource.Token);
  }

  private async Task StopUpdateLoop()
  {
    _stateGetterLoopTokenSource.Cancel();
    await _stateUpdater;
  }

  protected async Task<SerialDeviceValidationResult> Validate()
  {
    var request = new GenericLineRequest(AssumedId);
    try
    {
      var response = await GetResponseWithRetry<GenericLineResponse, GenericLineRequest>(request, 5, TimeSpan.FromSeconds(10));
      if(response.Id == AssumedId)
        return new SerialDeviceValidationResult(true);

      // This should never happen, but in case it does lets throw an exception as it's important to figure out
      // why the response id did not match.
      _logger.LogError($"### ALICAT MFC {Name}: Requested a live data line for MFC with id of {AssumedId}, but got a response with id of {response.Id} ###");  
      throw new InvalidOperationException($"Requested a live data line for MFC with id of {AssumedId} but got a response with id of {response.Id}");
    }
    catch(TimeoutException)
    {
      _logger.LogError($"### ALICAT MFC {Name}: Could not get a valid response for MFC {Name} with an id of {AssumedId} within allotted time. ###");
      return new SerialDeviceValidationResult(false, $"Could not get a valid response for MFC {Name} with an id of {AssumedId} within allotted time.");
    }
  }

  private async Task<TResult> GetResponseWithRetry<TResult, TRequest>(TRequest request, int retries, TimeSpan timeout)
    where TResult : CommandResponse
    where TRequest : MfcCommandExpectingResponse<TResult>
  {
    while(retries >= 0)
    {
      try
      {
        var response = await Send(request, timeout);
        return response;
      }
      catch(TimeoutException)
      {
      }
      retries--;
    }

    throw new TimeoutException($"Timed out while trying to get {request.GetType().Name}. Id : {request.MfcId}");
  }

  private void UpdateLiveData(LiveDataResponse liveResponse)
  {
    _liveData = liveResponse;
    var next = AresStateBuilder
      .From(_stateSubject.Value)
      .AddStruct("LiveData", b =>
      {
        b
         .Add("Temperature", liveResponse.Temperature?.Value ?? 0)
         .Add("MassFlow", liveResponse.MassFlow?.Value ?? 0)
         .Add("Setpoint", liveResponse.Setpoint?.Value ?? 0)
         .AddList(
          key: "StatusCodes",
          items: liveResponse.StatusCodes,
          mapper: entry =>
            new AresValue
            {
              StringValue = entry.ToString()
            });

        if(liveResponse.AbsolutePressure is not null)
          b.Add("AbsolutePressure", liveResponse.AbsolutePressure?.Value ?? 0);

        if(liveResponse.VolumetricFlow is not null)
          b.Add("VolumetricFlow", liveResponse.VolumetricFlow?.Value ?? 0);

        if(liveResponse.ValveDrive is not null)
          b.Add("ValveDrive", liveResponse.ValveDrive ?? 0);
      })
      .Add("ActiveGas", liveResponse.Gas ?? "Unknown").Build();

      _stateSubject.OnNext(next);
  }

  private void UpdateDataFrameFormat(DataFrameFormatEntry formatEntry)
  {
    // Removing this from state data, doesn't seem relevant
    if(formatEntry.EntryType is not DataFrameFormatEntryType.Entry)
      return;

    if(formatEntry.Id != AssumedId)
    {
      return; // TODO: Throw exception? This is causing issues.
    }

    var staleEntries = _dataFrameFormatEntries?.Where(entry => entry.EntryNumber >= formatEntry.EntryNumber).ToArray() ?? Array.Empty<DataFrameFormatEntry>();
    var existingEntries = new List<DataFrameFormatEntry>(_dataFrameFormatEntries ?? new());
    foreach(var staleEntry in staleEntries)
      existingEntries.Remove(staleEntry);

    existingEntries.Add(formatEntry);
    _dataFrameFormatEntries = existingEntries;
  }

  private void UpdateManufacturerInfo(ManufacturerInfoEntry manufactureEntry)
  {
    if(manufactureEntry.Id != AssumedId || _stateSubject.Value is null)
    {
      return; // TODO: Throw exception? This is causing issues.
    }

    _manufacturerInfo ??= new List<ManufacturerInfoEntry>();

    _manufacturerInfo.RemoveAll(
      entry => entry.EntryNumber >= manufactureEntry.EntryNumber);

    _manufacturerInfo.Add(manufactureEntry);


    var newState = AresStateBuilder
      .From(_stateSubject.Value)
      .AddList(
        key: "ManufacturerInfo",
        items: _manufacturerInfo,
        mapper: entry =>
          new AresValue
          {
            StructValue = AresStateBuilder.Create()
              .Add("EntryNumber", entry.EntryNumber)
              .Add("Manufacturer", entry.ManufacturerInfoEntryType.ToString())
              .Add("Data", entry.Data)
              .Add("IsEndMarker", entry.IsEndMarker)
              .Add("Id", entry.Id)
              .Build()
          })
      .Build();

    _stateSubject.OnNext(newState);
  }

  private void UpdateGasInfo(GasInfoEntry gasEntry)
  {
    if(gasEntry.IsEndMarker)
      return;

    var staleEntries = _gases?.Where(entry => entry.Index >= gasEntry.Index) ?? Array.Empty<GasInfoEntry>();
    var existingEntries = new List<GasInfoEntry>(_gases ?? new());
    foreach(var staleEntry in staleEntries)
      existingEntries.Remove(staleEntry);

    existingEntries.Add(gasEntry);
    _gases = existingEntries;

    if(_stateSubject.Value is null)
      return;

    var newState = AresStateBuilder
      .From(_stateSubject.Value)
      .AddList(
        key: "Gases",
        items: _gases,
        mapper: entry =>
        new AresValue
        {
          StructValue = AresStateBuilder.Create()
          .Add("Gas", entry.Gas)
          .Add("Index", entry.Index)
          .Add("IsEndMarker", entry.IsEndMarker)
          .Add("Id", entry.Id)
          .Add("RequestId", entry.RequestId.ToString())
          .Build()
        }
      ).Build();

    _stateSubject.OnNext(newState);
  }

  private DataFrameFormatEntry[] GetFormatEntries()
  {
    var dataFormatEntries = _dataFrameFormatEntries?.Where(entry => entry is not null).ToArray() ?? Array.Empty<DataFrameFormatEntry>();
    return dataFormatEntries!;
  }

  private Task<T> Send<T>(MfcCommandExpectingResponse<T> command) where T : CommandResponse
  {
    return _serialConnection.Send(command, TimeSpan.FromSeconds(10));
  }

  private Task<T> Send<T>(MfcCommandExpectingResponse<T> command, TimeSpan timeout) where T : CommandResponse
  {
    if(command.MfcId != AssumedId)
    {
      _logger.LogError($"Attempted to send command improperly. {command.MfcId} != {AssumedId}");
      throw new InvalidOperationException($"Attempting to send command improperly. {command.MfcId} != {AssumedId}");
    }
    return _serialConnection.Send(command, timeout);
  }

  public async Task Start()
  {
    await StopUpdateLoop();
    await StartUpdateLoop(TimeSpan.FromMilliseconds(500));
  }

  
  public override async Task<CommandResult> ExecuteCommand(string command, List<DeviceCommandArgument> arguments, CancellationToken token)
  {
    if(!Enum.TryParse<MassFlowControllerCommand>(command, out var deviceCommandEnum))
    {
      return new CommandResult
      {
        Success = false,
        Error = $"Invalid or unsupported command: '{command}'"
      };
    }

    var result = new CommandResult { Success = true };

    // Helper function to safely extract an argument by name (assuming DeviceCommandArgument has Name and Value properties)
    AresValue? GetArg(MassFlowControllerCommandParameter param) =>
        arguments.FirstOrDefault(a => a.ArgName == param.ToString())?.ArgValue;

    try
    {
      // Route the command
      switch(deviceCommandEnum)
      {
        case MassFlowControllerCommand.PollLiveDataFrame:
          // TODO: stringify live info
          break;

        case MassFlowControllerCommand.ManufacturerInfo:
          // await Device.GetManufacturerDataInfoAsync(token);
          break;

        case MassFlowControllerCommand.CancelValveHold:
          await CancelValveHold();
          break;

        case MassFlowControllerCommand.ChooseDifferentGas:
          var arg = arguments.FirstOrDefault(a => a.ArgName == MassFlowControllerCommandParameter.GasNumber.ToString());
          if(arg is null)
            return ArgumentError("ChooseDifferentGas", "GasNumber", "number");

          var found = arg.ArgValue.TryGetNumericValue(out var gasNum);

          if(!found)
            return ArgumentError("ChooseDifferentGas", "GasNumber", "number");

          await ChooseDifferentGas((int)gasNum);
          break;

        case MassFlowControllerCommand.DeleteComposerMix:
          var mixNumArg = arguments.FirstOrDefault(a => a.ArgName == MassFlowControllerCommandParameter.MixNumber.ToString());
          if(mixNumArg is null)
            return ArgumentError("DeleteComposerMix", "MixNumber", "number");
          
          var mixNumFound = mixNumArg.ArgValue.TryGetNumericValue(out var mixNum);

          if(!mixNumFound)
            return ArgumentError("DeleteComposerMix", "MixNumber", "number");

          await DeleteComposerMix((int)mixNum);
          break;

        case MassFlowControllerCommand.HoldValvesAtCurrentPosition:
          await HoldValvesAtCurrentPosition();
          break;

        case MassFlowControllerCommand.HoldValvesClosed:
          // Note: Your old code called CancelValveHold() here. Ensure this wasn't a typo in the original!
          await CancelValveHold();
          break;

        case MassFlowControllerCommand.NewComposerMix:
          throw new NotImplementedException("NewComposerMix is not yet implemented.");

        case MassFlowControllerCommand.NewSetpoint:
          var setpointArg = arguments.FirstOrDefault(a => a.ArgName == MassFlowControllerCommandParameter.Setpoint.ToString());

          if(setpointArg is null)
            return ArgumentError("NewSetpoint", "Setpoint", "number");

          var setpointFound = setpointArg.ArgValue.TryGetNumericValue(out var setpoint);

          if(!setpointFound)
            return ArgumentError("NewSetpoint", "Setpoint", "number");

          _logger.LogInformation($"Attempting to set new setpoint for MFC {Name} to {setpoint} sccm");
            await NewSetpoint(StandardVolumeFlow.FromStandardCubicCentimetersPerMinute(setpoint));
          break;

        case MassFlowControllerCommand.GetSetpoint:
          var setpt = _liveData?.Setpoint?.Value ?? double.MinValue;
          result.Result = AresValueHelper.CreateNumber(setpt);
          break;

        case MassFlowControllerCommand.TareAbsolutePressureWithBarometer:
          await TareAbsolutePressureWithBarometer();
          break;

        case MassFlowControllerCommand.TareFlow:
          await TareFlow();
          break;

        default:
          return new CommandResult
          {
            Success = false,
            Error = $"Command '{deviceCommandEnum}' is defined but execution logic is missing."
          };
      }
    }
    catch(Exception ex)
    {
      result.Success = false;
      result.Error = ex.Message;
    }

    return result;
  }

  private static CommandResult ArgumentError(string commandName, string paramName, string expectedType)
  {
    return new CommandResult
    {
      Success = false,
      Error = $"The {commandName} command requires a valid {expectedType} for '{paramName}', but none was provided or the type was incorrect."
    };
  }

  public override async Task UpdateSettings(AresStruct settings)
  {
    settings.Fields.TryGetValue("Selected Gas", out var selectedGas);

    if(selectedGas is { HasStringValue: true, StringValue: var newGas } && newGas != _liveData?.Gas)
    {
      var gasNumber = _gases.FindIndex(g => g.Gas == newGas);
      await ChooseDifferentGas(gasNumber);
    }

    if(_mfcType == MfcTypeEnum.Basis2)
    {
      settings.Fields.TryGetValue("Setpoint Source", out var setpointSource);

      if(setpointSource is { HasStringValue: true, StringValue: var source }  && source != _setpointSource.ToString())
      {
        if(Enum.TryParse<MfcSetpointSourceEnum>(setpointSource.StringValue, out var parsedSource))
          await SetSetpointSource(parsedSource);
      }
    }
  }

  public override async Task<AresStruct> GetSettings()
  {
    var response = AresStructHelper.CreateStringStruct("Selected Gas", _liveData?.Gas ?? "Unknown");
    
    if(_mfcType == MfcTypeEnum.Basis2)
      response.AddString("Setpoint Source", _setpointSource.ToString());

    return response;
  }

  protected override Task<List<DeviceCommandDescriptor>> BuildCommandDescriptorsAsync()
  {
    var descriptors = new List<DeviceCommandDescriptor>
    {
        new()
        {
            Name = MassFlowControllerCommand.NewSetpoint.ToString(),
            Description = "Sets a new target mass flow",
            InputSchema = AresSchemaBuilder.Empty()
                .AddEntry(MassFlowControllerCommandParameter.Setpoint.ToString(),
                          AresSchemaBuilder.NumberEntry().Build())
                .Build()
        },
        new()
        {
            Name = MassFlowControllerCommand.ChangeUnitId.ToString(),
            Description = "Assigns the device a new letter ID",
            InputSchema = AresSchemaBuilder.Empty()
                .AddEntry(MassFlowControllerCommandParameter.DeviceId.ToString(),
                          AresSchemaBuilder.StringEntry().AsOptional().Build())
                .Build()
        },
        new()
        {
            Name = MassFlowControllerCommand.PollLiveDataFrame.ToString(),
            Description = "Queries the device for a live data entry containing device ID, temperature, flow, setpoint, and gas. Depending on the type of the MFC, it may also include pressure and other data items."
        },
        new()
        {
            Name = MassFlowControllerCommand.CancelValveHold.ToString(),
            Description = "Cancels holds on the device's valve(s)"
        },
        new()
        {
            Name = MassFlowControllerCommand.ChooseDifferentGas.ToString(),
            Description = "Changes the currently managed gas",
            InputSchema = AresSchemaBuilder.Empty()
                .AddEntry(MassFlowControllerCommandParameter.GasNumber.ToString(),
                          AresSchemaBuilder.StringEntry().AsOptional().Build())
                .Build()
        },
        new()
        {
            Name = MassFlowControllerCommand.GetSetpoint.ToString(),
            Description = "Gets the current setpoint of the MFC",
            OutputSchema = AresSchemaBuilder.NumberEntry()
                .WithDescription("Current setpoint")
                .Build()
        }
    };

    if(_mfcType == MfcTypeEnum.Basis2)
    {
      descriptors.Add(new DeviceCommandDescriptor
      {
        Name = MassFlowControllerCommand.HoldValvesClosed.ToString(),
        Description = "Holds the device's valve(s) at the given position",
        InputSchema = AresSchemaBuilder.Empty()
              .AddEntry(MassFlowControllerCommandParameter.ValvePercent.ToString(),
                        AresSchemaBuilder.NumberEntry().Build())
              .Build()
      });
    }

    if(_mfcType == MfcTypeEnum.Normal)
    {
      descriptors.AddRange(
      [
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.ManufacturerInfo.ToString(),
            Description = "Queries the manufacturer info"
        },
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.TareAbsolutePressureWithBarometer.ToString(),
            Description = "Tares the device's absolute pressure with barometer"
        },
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.TareFlow.ToString(),
            Description = "Tares the device's flow"
        },
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.HoldValvesAtCurrentPosition.ToString(),
            Description = "Holds the device's valve(s) at the current position"
        },
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.HoldValvesClosed.ToString(),
            Description = "Holds the device's valve(s) at the closed position"
        },
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.NewComposerMix.ToString(),
            Description = "Adds a new COMPOSER mix to the device's memory"
        },
        new DeviceCommandDescriptor
        {
            Name = MassFlowControllerCommand.DeleteComposerMix.ToString(),
            Description = "Deletes the indicated COMPOSER Mix number from the device's memory",
            InputSchema = AresSchemaBuilder.Empty()
                .AddEntry(MassFlowControllerCommandParameter.MixNumber.ToString(),
                          AresSchemaBuilder.StringEntry().AsOptional().Build())
                .Build()
        }
      ]);
    }

    return Task.FromResult(descriptors);
  }
}