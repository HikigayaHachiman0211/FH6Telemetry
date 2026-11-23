internal static class TelemetryTestCases
{
    public static void Register(Action<string, Action> run)
    {
        TelemetryModuleTests.Register(run);
        AutomaticShiftTests.Register(run);
        GamepadShiftInputTests.Register(run);
        DriveAssistLateralTests.Register(run);
        DriveAssistPedalsTests.Register(run);
        DriveAssistAnalysisTests.Register(run);
        DriveAssistTests.Register(run);
        StandaloneTelemetryTests.Register(run);
        VehicleCatalogTests.Register(run);
        TelemetryTests.Register(run);
    }
}
