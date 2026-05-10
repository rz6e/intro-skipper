// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;
using IntroSkipper;
using IntroSkipper.Configuration;
using IntroSkipper.Manager;
using IntroSkipper.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
var config = new PluginConfiguration();
builder.Configuration.Bind("IntroSkipper", config);

var dataDir = builder.Configuration["DataDirectory"]
    ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "introskipper");

var ffmpegPath = builder.Configuration["FFmpegPath"]
    ?? Environment.GetEnvironmentVariable("FFMPEG_PATH")
    ?? "ffmpeg"; // assumes ffmpeg is on PATH

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<PluginConfiguration>(_ => config);
builder.Services.AddSingleton<FileQueueManager>();
builder.Services.AddSingleton<AnalysisService>();

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Initialise the Plugin singleton (reads/creates the SQLite databases).
var logger = app.Services.GetRequiredService<ILogger<Plugin>>();
_ = new Plugin(dataDir, ffmpegPath, config, logger);

FFmpegWrapper.Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FFmpegWrapper");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
