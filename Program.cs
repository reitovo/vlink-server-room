using VLink.Private.GrpcRoomServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.Configure<HostOptions>(a => a.ShutdownTimeout = TimeSpan.FromSeconds(3));
builder.WebHost.UseKestrel();

var app = builder.Build();

app.MapGrpcService<RoomServiceImpl>();

app.Run();