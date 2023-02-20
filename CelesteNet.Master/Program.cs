using CelesteNet.Master.Controllers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options => {
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

Thread heartbeatLoop = new Thread(() => {
    while (true) {
        Thread.Sleep(5000);
        ServerList.Cleanup();
    }
});

heartbeatLoop.Start();

app.MapControllers();
app.Run();
