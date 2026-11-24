using Ares.Datamodel;
using Ares.Datamodel.Device;
using Ares.Datamodel.Extensions;
using Ares.Datamodel.Factories;
using Ares.Device;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace GenericPIDController;

/// <summary>
/// A reusable, bounded PID controller. Call <see cref="Calculate"/> at a fixed
/// interval (or provide the actual elapsed interval) to produce the control output.
/// </summary>
public sealed class PIDController : AresDevice, IPIDController
{
  public const string KpSettingKey = "Kp";
  public const string KiSettingKey = "Ki";
  public const string KdSettingKey = "Kd";
  public const string MinOutputSettingKey = "MinOutput";
  public const string MaxOutputSettingKey = "MaxOutput";
  public const string DerivativeFilterCoefficientSettingKey = "DerivativeFilterCoefficient";

  private readonly ILogger _logger;
  private readonly BehaviorSubject<AresStruct> _stateSubject = new(new AresStruct());
  private readonly object _sync = new();
  private double? _latestCalculatedValue;
  private Duration? _latestTimeDelta;
  private double? _latestSetpoint;
  private double? _latestMeasurement;
  private double _integral;
  private double _filteredDerivative;
  private bool _hasDerivative;
  private bool _settingsAreValid;
  private bool _disposed;
  private const double DefaultDerivativeFilterCoefficient = 1;
  private const double MaximumIntegralMagnitude = double.MaxValue / 4;

  public PIDController(DeviceConnectionInfo connectionInfo, ILogger logger) : base(connectionInfo)
  {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _settingsAreValid = TryApplySettings(connectionInfo.DeviceSettings, out var error);

    if(!_settingsAreValid)
      _logger.LogWarning("PID controller settings are invalid: {Error}", error);

    Type = "Generic PID Controller";
    Description = "A software PID controller with output limits and derivative filtering.";
    HardwareIdentity = "GenericPIDController";
    Version = "1.0.0";

    SettingSchema
      .AddEntry(KpSettingKey, AresDataType.Number, false)
      .AddEntry(KiSettingKey, AresDataType.Number, false)
      .AddEntry(KdSettingKey, AresDataType.Number, false)
      .AddEntry(MinOutputSettingKey, AresDataType.Number, false)
      .AddEntry(MaxOutputSettingKey, AresDataType.Number, false)
      .AddEntry(DerivativeFilterCoefficientSettingKey, AresDataType.Number, true);

    StateSchema
      .AddEntry("Setpoint", AresDataType.Number, false)
      .AddEntry("Measurement", AresDataType.Number, false)
      .AddEntry("Error", AresDataType.Number, false)
      .AddEntry("Output", AresDataType.Number, false)
      .AddEntry("Integral", AresDataType.Number, false)
      .AddEntry("Derivative", AresDataType.Number, false)
      .AddEntry("TimeDeltaSeconds", AresDataType.Number, false);

    StateStream = _stateSubject.AsObservable();
    _stateSubject.OnNext(CreateInitialState());
  }

  public override Task<bool> Activate(CancellationToken ct)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ct.ThrowIfCancellationRequested();

    lock(_sync)
    {
      if(!_settingsAreValid)
      {
        Status = new DeviceOperationalStatus
        {
          OperationalState = OperationalState.Error,
          Message = "PID controller settings are invalid. Configure Kp, Ki, Kd, MinOutput, and MaxOutput."
        };
        return Task.FromResult(false);
      }

      ResetCore();
      Status = new DeviceOperationalStatus
      {
        OperationalState = OperationalState.Active,
        Message = "PID controller is active."
      };
      return Task.FromResult(true);
    }
  }

  public double Calculate(double setpoint, double measurement, Duration timeDelta)
  {
    ArgumentNullException.ThrowIfNull(timeDelta);

    if(!double.IsFinite(setpoint) || !double.IsFinite(measurement))
      throw new ArgumentOutOfRangeException(nameof(measurement), "Setpoint and measurement must be finite numbers.");

    var deltaSeconds = timeDelta.Seconds + (timeDelta.Nanos / 1_000_000_000d);
    if(!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || timeDelta.Nanos is < 0 or >= 1_000_000_000)
      throw new ArgumentOutOfRangeException(nameof(timeDelta), "Time delta must be a positive, normalized duration.");

    AresStruct state;
    double output;
    lock(_sync)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if(!_settingsAreValid)
        throw new InvalidOperationException("PID controller settings are invalid.");

      var error = setpoint - measurement;
      if(!double.IsFinite(error))
        throw new ArithmeticException("The PID error exceeded the finite numeric range.");

      var rawDerivative = _latestMeasurement.HasValue ? -(measurement - _latestMeasurement.Value) / deltaSeconds : 0;
      if(!double.IsFinite(rawDerivative))
        throw new ArithmeticException("The PID derivative exceeded the finite numeric range.");

      if(!_hasDerivative)
      {
        _filteredDerivative = rawDerivative;
        _hasDerivative = true;
      }

      else
      {
        _filteredDerivative = DerivativeFilterCoefficient * rawDerivative + (1 - DerivativeFilterCoefficient) * _filteredDerivative;
        if(!double.IsFinite(_filteredDerivative))
          throw new ArithmeticException("The filtered PID derivative exceeded the finite numeric range.");
      }

      // Conditional integration prevents wind-up when the output is saturated.
      var proposedIntegral = _integral + error * deltaSeconds;
      if(!double.IsFinite(proposedIntegral))
        proposedIntegral = Math.CopySign(MaximumIntegralMagnitude, proposedIntegral);

      proposedIntegral = Clamp(proposedIntegral, -MaximumIntegralMagnitude, MaximumIntegralMagnitude);
      var proportionalAndDerivative = Kp * error + Kd * _filteredDerivative;

      if(!double.IsFinite(proportionalAndDerivative))
        throw new ArithmeticException("The PID proportional/derivative term exceeded the finite numeric range.");

      var proposedOutput = proportionalAndDerivative + Ki * proposedIntegral;
      if(double.IsNaN(proposedOutput))
        throw new ArithmeticException("The PID output was not a number.");

      if(double.IsInfinity(proposedOutput))
        proposedOutput = Math.CopySign(double.MaxValue, proposedOutput);

      output = Clamp(proposedOutput, MinOutput, MaxOutput);

      var isSaturatedHigh = proposedOutput > MaxOutput;
      var isSaturatedLow = proposedOutput < MinOutput;
      var integralDirection = Ki * error;
      var integrationMovesOutputDown = integralDirection < 0;
      var integrationMovesOutputUp = integralDirection > 0;
      if(Ki != 0 && (!isSaturatedHigh && !isSaturatedLow || isSaturatedHigh && integrationMovesOutputDown || isSaturatedLow && integrationMovesOutputUp))
      {
        _integral = proposedIntegral;
        output = Clamp(proportionalAndDerivative + Ki * _integral, MinOutput, MaxOutput);
      }

      if(!double.IsFinite(output))
        throw new ArithmeticException("The PID output exceeded the finite numeric range.");

      _latestCalculatedValue = output;
      _latestSetpoint = setpoint;
      _latestMeasurement = measurement;
      _latestTimeDelta = timeDelta.Clone();
      state = CreateState(error, deltaSeconds);
    }

    _stateSubject.OnNext(state);
    return output; 
  }

  public override Task EnterSafeMode(CancellationToken ct)
  {
    if(!_disposed)
    {
      ct.ThrowIfCancellationRequested();
      Reset();
    }

    return Task.CompletedTask;
  }

  public override Task<CommandResult> ExecuteCommand(string command, List<DeviceCommandArgument> arguments, CancellationToken token)
  {
    if(string.Equals(command, "Reset", StringComparison.OrdinalIgnoreCase))
    {
      try
      {
        token.ThrowIfCancellationRequested();
        Reset();
        return Task.FromResult(new CommandResult { Success = true });
      }
      catch(Exception ex)
      {
        return Task.FromResult(new CommandResult { Success = false, Error = ex.Message });
      }
    }

    else if(string.Equals(command, "Calculate", StringComparison.OrdinalIgnoreCase))
    {
      try
      {
        token.ThrowIfCancellationRequested();
        var measurement = arguments.FirstOrDefault(d => d.ArgName == PIDControllerCommandParameter.Measurement.ToString());
        var setpoint = arguments.FirstOrDefault(d => d.ArgName == PIDControllerCommandParameter.Setpoint.ToString());
        var startTime = arguments.FirstOrDefault(d => d.ArgName == PIDControllerCommandParameter.StartTime.ToString());
        var endTime = arguments.FirstOrDefault(d => d.ArgName == PIDControllerCommandParameter.EndTime.ToString());

        if(measurement is null || setpoint is null || startTime is null || endTime is null)
          return Task.FromResult(new CommandResult { Success = false, Error = "Calculation requries all four parameters to be performed!" });

        var startTimeStamp = startTime.ArgValue.TimestampValue;
        var endTimeStamp = endTime.ArgValue.TimestampValue;

        var delta = endTimeStamp - startTimeStamp;
        var setpointValueFound = setpoint.ArgValue.TryGetNumericValue(out var setpointValue);
        var measurementValueFound = measurement.ArgValue.TryGetNumericValue(out var measurementValue);

        if(setpointValueFound && measurementValueFound)
        {
          var response = Calculate(setpointValue, measurementValue, delta);

          return Task.FromResult(new CommandResult { Success = true, Result = AresValueHelper.CreateFloat(response) });
        }

      }

      catch(Exception ex)
      {
        return Task.FromResult(new CommandResult { Success = false, Error = ex.Message });
      }
    }

    return Task.FromResult(new CommandResult { Success = false, Error = "Unrecognized command received!" });
  }

  public override Task<AresStruct> GetSettings()
  {
    lock(_sync)
      return Task.FromResult(CreateSettingsStruct());
  }

  public override Task<AresStruct> GetState()
  {
    lock(_sync)
      return Task.FromResult(_stateSubject.Value.Clone());
  }

  public void Reset()
  {
    AresStruct state;
    lock(_sync)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      state = ResetCore();
    }

    _stateSubject.OnNext(state);    
  }

  public override Task UpdateSettings(AresStruct settings)
  {
    ArgumentNullException.ThrowIfNull(settings);

    AresStruct state;
    lock(_sync)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if(!TryApplySettings(settings, out var error))
        throw new ArgumentException(error, nameof(settings));

      _settingsAreValid = true;
      state = ResetCore();
    }

    _stateSubject.OnNext(state);
    
    return Task.CompletedTask;
  }

  protected override Task<List<DeviceCommandDescriptor>> BuildCommandDescriptorsAsync()
  {
    return Task.FromResult(new List<DeviceCommandDescriptor>
    {
      new()
      {
        Name = "Reset",
        Description = "Clears the PID integral, derivative, and previous-sample state."
      },
      new()
      {
        Name = "Calculate",
        Description = "Request a new PID calculation be performed with the provided information",
        InputSchema = AresSchemaBuilder.Empty()
        .AddEntry(PIDControllerCommandParameter.Measurement.ToString(), AresSchemaBuilder.FloatEntry().Build()).WithDescription("The current measured/processed value")
        .AddEntry(PIDControllerCommandParameter.Setpoint.ToString(), AresSchemaBuilder.FloatEntry().Build()).WithDescription("The desired target value")
        .AddEntry(PIDControllerCommandParameter.StartTime.ToString(), AresSchemaBuilder.TimestampEntry().Build()).WithDescription("A starting time for the provided measurement")
        .AddEntry(PIDControllerCommandParameter.EndTime.ToString(), AresSchemaBuilder.TimestampEntry().Build()).WithDescription("An ending time for the provided measurement")
        .Build(),
        OutputSchema = AresSchemaBuilder.FloatEntry()
                .WithDescription("The calculated setpoint from the PID controller")
                .Build()
      }
    });
  }

  public ValueTask DisposeAsync()
  {
    lock(_sync)
    {
      if(_disposed)
        return ValueTask.CompletedTask;

      _disposed = true;
      _stateSubject.OnCompleted();
      _stateSubject.Dispose();
    }
   
    return ValueTask.CompletedTask;
  }

  protected override void Dispose(bool disposing)
  {
    if(disposing)
      DisposeAsync().AsTask().GetAwaiter().GetResult();

    base.Dispose(disposing);
  }

  private bool TryApplySettings(AresStruct settings, out string error)
  {
    if(!TryGetNumber(settings, KpSettingKey, out var kp)
      || !TryGetNumber(settings, KiSettingKey, out var ki)
      || !TryGetNumber(settings, KdSettingKey, out var kd)
      || !TryGetNumber(settings, MinOutputSettingKey, out var minOutput)
      || !TryGetNumber(settings, MaxOutputSettingKey, out var maxOutput))
    {
      error = "Kp, Ki, Kd, MinOutput, and MaxOutput must be finite numeric settings.";
      return false;
    }

    var derivativeFilter = DerivativeFilterCoefficient;
    if(settings.Fields.TryGetValue(DerivativeFilterCoefficientSettingKey, out var derivativeFilterValue))
    {
      if(!TryGetNumber(derivativeFilterValue, out derivativeFilter))
      {
        error = "DerivativeFilterCoefficient must be a finite number between zero and one.";
        return false;
      }
    }

    if(minOutput > maxOutput)
    {
      error = "MinOutput must not exceed MaxOutput.";
      return false;
    }

    if(derivativeFilter is < 0 or > 1)
    {
      error = "DerivativeFilterCoefficient must be between zero and one.";
      return false;
    }

    Kp = kp;
    Ki = ki;
    Kd = kd;
    MinOutput = minOutput;
    MaxOutput = maxOutput;
    DerivativeFilterCoefficient = derivativeFilter;
    error = string.Empty;
    return true;
  }

  private AresStruct CreateSettingsStruct()
  {
    return AresStructHelper.CreateNumberStruct(KpSettingKey, Kp)
      .AddNumber(KiSettingKey, Ki)
      .AddNumber(KdSettingKey, Kd)
      .AddNumber(MinOutputSettingKey, MinOutput)
      .AddNumber(MaxOutputSettingKey, MaxOutput)
      .AddNumber(DerivativeFilterCoefficientSettingKey, DerivativeFilterCoefficient);
  }

  private AresStruct ResetCore()
  {
    _latestCalculatedValue = null;
    _latestTimeDelta = null;
    _latestSetpoint = null;
    _latestMeasurement = null;
    _integral = 0;
    _filteredDerivative = 0;
    _hasDerivative = false;
    return CreateInitialState();
  }

  private AresStruct CreateInitialState()
    => AresStructHelper.CreateNumberStruct("Setpoint", 0)
      .AddNumber("Measurement", 0)
      .AddNumber("Error", 0)
      .AddNumber("Output", 0)
      .AddNumber("Integral", 0)
      .AddNumber("Derivative", 0)
      .AddNumber("TimeDeltaSeconds", 0);

  private AresStruct CreateState(double error, double deltaSeconds)
    => AresStructHelper.CreateNumberStruct("Setpoint", _latestSetpoint!.Value)
      .AddNumber("Measurement", _latestMeasurement!.Value)
      .AddNumber("Error", error)
      .AddNumber("Output", _latestCalculatedValue!.Value)
      .AddNumber("Integral", _integral)
      .AddNumber("Derivative", _filteredDerivative)
      .AddNumber("TimeDeltaSeconds", deltaSeconds);

  private static bool TryGetNumber(AresStruct settings, string key, out double value)
  {
    value = default;
    return settings.Fields.TryGetValue(key, out var setting) && TryGetNumber(setting, out value);
  }

  private static bool TryGetNumber(AresValue setting, out double value)
  {
    value = default;
    if(setting.HasNumberValue)
      value = setting.NumberValue;
    else if(setting.HasFloatValue)
      value = setting.FloatValue;
    else
      return false;

    return double.IsFinite(value);
  }

  private static double Clamp(double value, double minimum, double maximum)
    => Math.Min(Math.Max(value, minimum), maximum);

  public override IObservable<AresStruct> StateStream { get; }
  private double Kp { get; set; }
  private double Ki { get; set; }
  private double Kd { get; set; }
  private double MinOutput { get; set; }
  private double MaxOutput { get; set; }

  // One uses the latest raw derivative; zero retains the previous filtered value.
  private double DerivativeFilterCoefficient { get; set; } = DefaultDerivativeFilterCoefficient;
}