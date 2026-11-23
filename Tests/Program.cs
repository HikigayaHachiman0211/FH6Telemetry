if (args is ["--ui"])
{
    StandaloneTelemetryWindowTests.Run();
    return 0;
}

var total = 0;
var failures = 0;
TelemetryTestCases.Register((name, test) =>
{
    total++;
    try
    {
        test();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine("FAIL " + name + " · " + error.Message);
    }
});
Console.WriteLine($"{total - failures}/{total} passed");
return failures == 0 ? 0 : 1;
