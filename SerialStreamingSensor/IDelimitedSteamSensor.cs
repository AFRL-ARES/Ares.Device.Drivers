namespace SerialStreamingSensor
{
    public interface IDelimitedSteamSensor: IAsyncDisposable
    {


        Task BeginCollectingStats(string fieldName);
        Task EndCollectingStats(string fieldName);
        Task<long> getCollectionCount(string fieldName);
        Task<double> getMean(string fieldName);
        Task<double> getVariance(string fieldName);
        Task ResetStatistics(string fieldName);

    }
}
