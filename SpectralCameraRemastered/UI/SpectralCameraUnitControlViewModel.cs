using Ares.Toolkit.Device.UI;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SpectralCameraRemastered.Commands;
using SpectralCameraRemastered.UI.Mappers;
using SpectralCameraRemastered.UI.Models;
using System.Reactive;
using System.Reactive.Linq;

namespace SpectralCameraRemastered.UI;

public partial class SpectralCameraUnitControlViewModel : DeviceUnitControlViewModel<SpectralCameraDevice>
{
    private IDisposable? _stateSubscription;
    private readonly ILogger<SpectralCameraUnitControlViewModel> _logger;

    [Reactive] public partial SpectralCameraUIState CameraState { get; set; } = new();
    [Reactive] public partial double TargetExposureTime { get; set; }
    [Reactive] public partial double TargetTempSetpoint { get; set; }
    [Reactive] public partial int Iterations { get; set; } = 1;

    public ReactiveCommand<Unit, Unit> StartAcqCommand { get; }
    public ReactiveCommand<Unit, Unit> AbortAcqCommand { get; }
    public ReactiveCommand<Unit, Unit> SingleScanCommand { get; }
    public ReactiveCommand<Unit, Unit> ContinuousScanCommand { get; }

    public SpectralCameraUnitControlViewModel(SpectralCameraDevice device, ILogger<SpectralCameraUnitControlViewModel> logger) : base(device)
    {
        _logger = logger;
        
        _stateSubscription = device.StateStream
            .Select(SpectralCameraStateMapper.FromAresStruct)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(s => CameraState = s);

        StartAcqCommand = ReactiveCommand.CreateFromTask(() => Device.ExecuteCommand(SpectralCameraCommand.StartAcq.ToString(), new(), CancellationToken.None));
        AbortAcqCommand = ReactiveCommand.CreateFromTask(() => Device.ExecuteCommand(SpectralCameraCommand.AbortAcq.ToString(), new(), CancellationToken.None));
        
        SingleScanCommand = ReactiveCommand.CreateFromTask(() => 
            Device.ExecuteCommand(SpectralCameraCommand.RamanSingScan.ToString(), 
                new() { new DeviceCommandArgument { ArgName = SpectralCameraCommandParameter.Iterations.ToString(), ArgValue = new() { NumberValue = Iterations } } }, 
                CancellationToken.None));

        ContinuousScanCommand = ReactiveCommand.CreateFromTask(() => 
            Device.ExecuteCommand(SpectralCameraCommand.RamanContScan.ToString(), new(), CancellationToken.None));

        ViewType = typeof(SpectralCameraControl);
        DefaultWidth = 30;
    }

    public override async Task UpdateSettings(Ares.Datamodel.AresStruct settings)
    {
        await Device.UpdateSettings(settings);
    }

    protected override void Dispose(bool disposing)
    {
        _stateSubscription?.Dispose();
        base.Dispose(disposing);
    }
}
