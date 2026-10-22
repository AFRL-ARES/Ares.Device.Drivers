using Ares.Toolkit.Device.UI;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SerialStreamingSensor.UI.State;
using System.Reactive.Linq;

namespace SerialStreamingSensor.UI
{
    public sealed class DelimitedStreamSensorControlViewModel
        : DeviceUnitControlViewModel<DelimitedStreamSensor>,
          IAsyncDisposable
    {
        private readonly ILogger<
            DelimitedStreamSensorControlViewModel> _logger;

        private IDisposable? _stateSubscription;

        private DelimitedStreamSensorState _sensorState = new();
        private bool _hasValidData;
        private bool _capturingLiveData;
        private bool _disposed;

        public DelimitedStreamSensorControlViewModel(
            DelimitedStreamSensor sensor,
            ILogger<DelimitedStreamSensorControlViewModel> logger)
            : base(sensor)
        {
            _logger = logger;

            ViewType = typeof(DelimitedStreamSensorControl);
            DefaultWidth = 19;

            _stateSubscription = sensor.StateStream
                .Select(
                    DelimitedStreamSensorStateMapper.FromAresStruct)
                .Subscribe(
                    UpdateState,
                    HandleStateStreamError,
                    HandleStateStreamCompleted);
        }

        public DelimitedStreamSensorState SensorState
        {
            get => _sensorState;

            private set =>
                this.RaiseAndSetIfChanged(
                    ref _sensorState,
                    value);
        }

        public bool HasValidData
        {
            get => _hasValidData;

            private set =>
                this.RaiseAndSetIfChanged(
                    ref _hasValidData,
                    value);
        }

        public bool CapturingLiveData
        {
            get => _capturingLiveData;

            private set =>
                this.RaiseAndSetIfChanged(
                    ref _capturingLiveData,
                    value);
        }

        private void UpdateState(
            DelimitedStreamSensorState? state)
        {
            if (state is null)
            {
                HasValidData = false;
                CapturingLiveData = false;
                return;
            }

            SensorState = state;
            HasValidData = true;
            CapturingLiveData = true;
        }

        private void HandleStateStreamError(
            Exception exception)
        {
            _logger.LogError(
                exception,
                "State stream failed for {Name}",
                DeviceName);

            HasValidData = false;
            CapturingLiveData = false;
        }

        private void HandleStateStreamCompleted()
        {
            CapturingLiveData = false;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;

            _stateSubscription?.Dispose();
            _stateSubscription = null;

            GC.SuppressFinalize(this);

            return ValueTask.CompletedTask;
        }
    }
}