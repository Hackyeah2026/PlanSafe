// Fail startup if the bundled SQLite native library cannot be loaded.
SQLitePCL.Batteries_V2.Init();
_ = SQLitePCL.raw.sqlite3_libversion();

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
app.MapGet("/api/health", () => Results.Text("OK"));
app.Run();
