using Ares.Datamodel;
using Google.Protobuf.WellKnownTypes;

namespace GenericPIDController;

public interface IPIDController : IAsyncDisposable
{
  void Reset();
  double Calculate(double setpoint, double measurement, Duration timeDelta);
}
