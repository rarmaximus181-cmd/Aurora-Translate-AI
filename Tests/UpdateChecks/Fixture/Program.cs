// This disposable test app has no UI, network, or access to Aurora's real settings.
System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "restart-marker.txt"), "2.1.0");
