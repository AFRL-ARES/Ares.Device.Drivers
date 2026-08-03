using Ares.Datamodel;
using Ares.Datamodel.Device;
using Ares.Datamodel.Extensions;
using Ares.Datamodel.Factories;
using Ares.Device;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using SerialStreamingSensor.Connection;
using SerialStreamingSensor.Models;
using StreamHelper;
using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace SerialStreamingSensor
{
    public class DelimitedStreamSensor : AresDevice, IDelimitedSteamSensor
    {
        private readonly DeviceConnectionInfo _connectionInfo;
        private readonly ILogger _logger;
        private readonly string _dataFormat;
        private readonly string[] _fields;
        private readonly StreamingField[] _streamingFields;
        private readonly IReadOnlyDictionary<string, StreamingField> _streamingFieldsByName;
        private readonly System.IO.Ports.SerialPort _serialConnection;
        private CancellationTokenSource _stateGetterLoopTokenSource = new();
        //private CompositeDisposable _stateWatchers = new();
        private Task _stateUpdater = Task.CompletedTask;

        private readonly BehaviorSubject<AresStruct> _stateSubject = new(new AresStruct());

        public DelimitedStreamSensor(DeviceConnectionInfo info, ILogger logger) : base(info)
        {
            
            _connectionInfo = info;
            _logger = logger;
            _dataFormat = info.DeviceSettings.Fields["DataFormat"].StringValue;
            StateStream = _stateSubject.AsObservable();

            _logger.LogInformation($"Parsing data format '{_dataFormat}'");
            _serialConnection = new System.IO.Ports.SerialPort()
            {
                PortName = info.SerialConnectionInfo.PortName,
                BaudRate = 115200,
                Parity = System.IO.Ports.Parity.None,
                StopBits = System.IO.Ports.StopBits.One,
                DataBits = 8,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout=1000,
            };



            _fields = _dataFormat.Split(":,\t".ToCharArray(), StringSplitOptions.None).Select(field => field.Trim()).ToArray();

            _streamingFields = _fields
                .Select((fieldName, dataIndex) => new
                {
                    FieldName = fieldName,
                    DataIndex = dataIndex
                })
                .Where(field => !string.IsNullOrWhiteSpace(field.FieldName))
                .Select(field => new StreamingField
                {
                    Name = field.FieldName,
                    DataIndex = field.DataIndex,
                    StatsActive = false,
                    Value = null
                })
                .ToArray();

            _logger.LogInformation($"{_streamingFields.Length} fields parsed");

            _streamingFieldsByName = _streamingFields.ToDictionary(field => field.Name, field => field, StringComparer.OrdinalIgnoreCase);

            StateSchema = AresSchemaBuilder.Empty()
                .AddEntry("Name", AresSchemaBuilder.StringEntry().Build())
                .AddEntry("DataFormat", AresSchemaBuilder.StringEntry().Build())
                .AddEntry("LiveData", AresSchemaBuilder.Entry(AresDataType.Struct)
                    .WithStructSchema(liveData =>
                    {
                        foreach (var field in _streamingFields)
                        {
                            liveData.Fields.Add(field.Name, AresSchemaBuilder.NumberEntry().AsOptional().Build());
                        }
                    })
                    .Build())
                .Build();

            //_stateWatchers = new CompositeDisposable
            //    {
            //      _serialConnection.GetTransactionStream<ReadLineResponse>().Select(transaction => transaction.Response).Subscribe(UpdateLiveData)
            //    };

            _logger.LogInformation($" Streaming device {Name} initialization completed");
        }

        private void UpdateLiveData(string response)
        {
            // TODO: move parsing into parser class (deliver key-value pairs instead of raw line)
            //_logger.LogInformation($"Received line: {response}");
            var fields = response.Split(":,\t".ToCharArray(), StringSplitOptions.None).Select(field => field.Trim()).ToArray();
            foreach (var field in _streamingFields)
            {
                double value;
                if (double.TryParse(fields[field.DataIndex], out value))
                {
                    field.Value = value;
                    if (field.StatsActive) field.Stats.AddValue(value);    
                }
                else
                {
                    field.Value = null;
                }
            }

            var next = AresStateBuilder
                  .From(_stateSubject.Value)
                  .AddStruct("LiveData", b =>
                  {
                      foreach (var field in _streamingFields)
                      {
                          b.Add(field.Name, field.Value ?? 0.0);
                      }
                  })
                  .Build();

            _stateSubject.OnNext(next);
        }

        private AresStruct BuildInitialState()
        {
            return AresStateBuilder.Create()
                .Add("Name", Name)
                .Add("DataFormat", _dataFormat)
                .AddStruct("LiveData", liveData =>
                {
                    foreach (var field in _streamingFields)
                    {
                        /*
                         * If AresStateBuilder does not support null numeric values,
                         * use 0 initially and distinguish validity separately.
                         */
                        liveData.Add(field.Name, field.Value ?? 0.0);
                    }
                })
                .Build();
        }

        public override IObservable<AresStruct> StateStream { get; }


        public async override Task<bool> Activate(CancellationToken ct)
        {
            bool activated = false;
            _logger.LogInformation($"Activating streaming device {Name}...");
            try
            {
                await Initialize();
                activated = true;
                Status = new DeviceOperationalStatus { OperationalState = OperationalState.Active, Message = $"Streaming device {Name} is active!" };
                _logger.LogInformation($"Device {Name} activated");
            }
            catch (Exception e)
            {
                Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"Failed to initialize: {e.Message}" };
                _logger.LogError(e, $"Device {Name} activation failed");
            }

            return activated;
        }

        private async Task Initialize()
        {
            if (_serialConnection is null)
            {
                _logger.LogError($"Serial device {Name}: Initialize was called, but connection was not set!");
                return;
            }

            await StopUpdateLoop();

            _stateSubject.OnNext(BuildInitialState());

            _ = Start();

        }

        public async Task Start()
        {
            await StopUpdateLoop();
            await StartUpdateLoop(TimeSpan.FromMilliseconds(500));
        }

        public async Task StartUpdateLoop(TimeSpan interval)
        {
            await StopUpdateLoop();
            await Task.Delay(150);
            _stateGetterLoopTokenSource = new CancellationTokenSource();
            _stateUpdater = Task.Factory.StartNew(async _ =>
            {
                Thread.CurrentThread.Name = $"Serial Device {Name} State Update Loop Thread";
                try
                {
                    while (!_stateGetterLoopTokenSource.IsCancellationRequested)
                    {
                        try
                        {
                            if (!_serialConnection.IsOpen) _serialConnection.Open();
                            //_logger.LogInformation($"Requesting live data at {DateTime.Now}");
                            var liveData = _serialConnection.ReadLine();
                            UpdateLiveData(liveData);
                        }
                        catch (TimeoutException)
                        {
                            _logger.LogError($"Get Live Data timed out at {DateTime.Now}");
                            Status = new DeviceOperationalStatus { OperationalState = OperationalState.Active, Message = $"Get Live Data timed out at {DateTime.Now}" };
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Get Live Data failed at {DateTime.Now}");
                            await Task.Delay(150);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception e)
                {
                    Status = new DeviceOperationalStatus { OperationalState = OperationalState.Error, Message = $"{e.Message}" };
                }
            },
              _stateGetterLoopTokenSource.Token);
        }

        private async Task StopUpdateLoop()
        {
            _stateGetterLoopTokenSource?.Cancel();
            await _stateUpdater;
        }

        public Task BeginCollectingStats(string fieldName)
        {
            var field = getStreamingField(fieldName);
            field.Stats.ResetStats();
            field.StatsActive = true;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            //_stateWatchers.Dispose();
            await _stateGetterLoopTokenSource.CancelAsync();
            await _stateUpdater;
            _stateGetterLoopTokenSource.Dispose();
            _stateSubject.OnCompleted();
        }

        public Task EndCollectingStats(string fieldName)
        {
            var field = getStreamingField(fieldName);
            field.StatsActive = false;
            return Task.CompletedTask;
        }

        public override Task EnterSafeMode(CancellationToken ct)
        {
            // passive device. No action required
            return Task.CompletedTask;
        }

        public async override Task<CommandResult> ExecuteCommand(string command, List<DeviceCommandArgument> arguments, CancellationToken token)
        {
            var result = new CommandResult { Success = true };
            string? fieldName = null;

            try
            {
                switch (command)
                {
                    case nameof(BeginCollectingStats):
                        fieldName = arguments.FirstOrDefault(a => a.ArgName == "fieldName")?.ArgValue.StringValue;
                        if (fieldName == null) return ArgumentError(nameof(BeginCollectingStats), "fieldName", "string");
                        await BeginCollectingStats(fieldName);
                        break;

                    case nameof(EndCollectingStats):
                        fieldName = arguments.FirstOrDefault(a => a.ArgName == "fieldName")?.ArgValue.StringValue;
                        if (fieldName == null) return ArgumentError(nameof(EndCollectingStats), "fieldName", "string");
                        await EndCollectingStats(fieldName);
                        break;

                    case nameof(ResetStatistics):
                        fieldName = arguments.FirstOrDefault(a => a.ArgName == "fieldName")?.ArgValue.StringValue;
                        if (fieldName == null) return ArgumentError(nameof(ResetStatistics), "fieldName", "string");
                        await ResetStatistics(fieldName);
                        break;
                    
                    case nameof(getCollectionCount):
                        fieldName = arguments.FirstOrDefault(a => a.ArgName == "fieldName")?.ArgValue.StringValue;
                        if (fieldName == null) return ArgumentError(nameof(getCollectionCount), "fieldName", "string");
                        var collectionCount = await getCollectionCount(fieldName);
                        result.Result=AresValueHelper.CreateNumber(collectionCount);
                        break;
                    
                    case nameof(getVariance):
                        fieldName = arguments.FirstOrDefault(a => a.ArgName == "fieldName")?.ArgValue.StringValue;
                        if (fieldName == null) return ArgumentError(nameof(getVariance), "fieldName", "string");
                        var variance = await getVariance(fieldName);
                        result.Result = AresValueHelper.CreateNumber(variance);
                        break;
                    
                    case nameof(getMean):
                        fieldName = arguments.FirstOrDefault(a => a.ArgName == "fieldName")?.ArgValue.StringValue;
                        if (fieldName == null) return ArgumentError(nameof(getMean), "fieldName", "string");
                        var mean = await getMean(fieldName) ;
                        result.Result = AresValueHelper.CreateNumber(mean);
                        break;
                    
                    default:
                        result.Success = false;
                        result.Error = $"Unrecognized command '{command}'";
                        break;
                }
            }
            catch (Exception err)
            {
                result.Success = false;
                result.Error = err.Message;
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

        public Task<long> getCollectionCount(string fieldName)
        {
            var field = getStreamingField(fieldName);
            var count = field.Stats.GetCount();
            return Task.FromResult(count);
        }

        public Task<double> getMean(string fieldName)
        {
            var field = getStreamingField(fieldName);
            var mean = field.Stats.GetMean();
            return Task.FromResult(mean);
        }

        public override Task<AresStruct> GetSettings()
        {
            var response = new AresStruct().AddString("Mode", "Streaming senor");
            return Task.FromResult(response);
        }

        public override Task<AresStruct> GetState() => Task.FromResult(_stateSubject.Value);

        public Task<double> getVariance(string fieldName)
        {
            var field = getStreamingField(fieldName);
            var variance = field.Stats.GetSampleVariance();
            return Task.FromResult(variance);
        }

        public Task ResetStatistics(string fieldName)
        {
            var field = getStreamingField(fieldName);
            field.Stats.ResetStats();
            return Task.CompletedTask;
        }

        StreamingField getStreamingField(string fieldName)
        {
            if (!_streamingFieldsByName.ContainsKey(fieldName))
            {
                throw new ArgumentOutOfRangeException($"'{fieldName}' not a recognized data field");
            }
            return _streamingFieldsByName[fieldName];
        }


        public override Task UpdateSettings(AresStruct settings)
        {
           return Task.CompletedTask;
        }

        protected override Task<List<DeviceCommandDescriptor>> BuildCommandDescriptorsAsync()
        {

            var descriptors = new List<DeviceCommandDescriptor>
            {
                new()
                {
                    Name = nameof(BeginCollectingStats),
                    Description = "Starts calculating stats on streaming data",
                    InputSchema = AresSchemaBuilder.Empty()
                        .AddEntry("fieldName", AresSchemaBuilder.StringEntry().Build())
                        .Build()
                },
                new()
                {
                    Name = nameof(EndCollectingStats),
                    Description = "Ends calculating stats on streaming data",
                    InputSchema = AresSchemaBuilder.Empty()
                        .AddEntry("fieldName", AresSchemaBuilder.StringEntry().Build())
                        .Build()
                },
                new()
                {
                    Name = nameof(getVariance),
                    Description = "gets the calculated variance",
                    InputSchema = AresSchemaBuilder.Empty()
                        .AddEntry("fieldName", AresSchemaBuilder.StringEntry().Build())
                        .Build(),
                    OutputSchema = AresSchemaBuilder.NumberEntry()
                        .WithDescription("variance")
                        .Build()
                },
                new()
                {
                    Name = nameof(getMean),
                    Description = "gets the calculated mean",
                    InputSchema = AresSchemaBuilder.Empty()
                        .AddEntry("fieldName", AresSchemaBuilder.StringEntry().Build())
                        .Build(),
                    OutputSchema = AresSchemaBuilder.NumberEntry()
                        .WithDescription("mean")
                        .Build()
                },
                new()
                {
                    Name = nameof(getCollectionCount),
                    Description = "gets the number of samples in stats calculation",
                    InputSchema = AresSchemaBuilder.Empty()
                        .AddEntry("fieldName", AresSchemaBuilder.StringEntry().Build())
                        .Build(),
                    OutputSchema = AresSchemaBuilder.NumberEntry()
                        .WithDescription("count")
                        .Build()
                },
                new()
                {
                    Name = nameof(ResetStatistics),
                    Description = "clears current stats data",
                    InputSchema = AresSchemaBuilder.Empty()
                        .AddEntry("fieldName", AresSchemaBuilder.StringEntry().Build())
                        .Build()
                },
            };

            return Task.FromResult(descriptors);

        }
    }
}
