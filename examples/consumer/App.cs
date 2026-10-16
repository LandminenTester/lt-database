// This demo runs once when this example bundle starts, on the server.
// It creates a schema, writes and reads a disposable sample, then removes that sample.
// It does not register player hooks and does not implement gameplay settings.
public static class App
{
    public static void Start() => Examples.PlayerPreferences.Run();
}
