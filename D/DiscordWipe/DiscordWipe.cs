﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

#if RUST
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using ConVar;
#endif

namespace Oxide.Plugins;

[Info("Discord Wipe", "MJSU", "2.4.3")]
[Description("Sends a notification to a discord channel when the server wipes or protocol changes")]
internal class DiscordWipe : CovalencePlugin
{
    #region Class Fields

    [PluginReference] private Plugin RustMapApi, PlaceholderAPI;
        
    private PluginConfig _pluginConfig; //Plugin Config
    private StoredData _storedData; //Plugin Data

    private const string DefaultUrl = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
    private const string DefaultRustMapsApiKey = "Get Your API Key @ https://rustmaps.com/dashboard";
    private const string AdminPermission = "discordwipe.admin";
    private const string AttachmentBase = "attachment://";
    private const string MapAttachment = AttachmentBase + MapFilename;
    private const string MapFilename = "map.jpg";
    private const int MaxImageSize = 8 * 1024 * 1024;
        
    private string _protocol;
    private string _previousProtocol;
    private bool _hasStarted;

    private readonly StringBuilder _parser = new();
    private Action<IPlayer, StringBuilder, bool> _replacer;
        
    private enum DebugEnum {Message, None, Error, Warning, Info}
    public enum SendMode {Always, Random}
    private enum EncodingMode {Jpg = 1, Png = 2}

#if RUST
    private enum RustMapMode {None, RustMaps, RustMapApi}
    private RustMapsResponse _rustMapsResponse;
#endif
    #endregion

    #region Setup & Loading
    private void Init()
    {
        UnsubscribeAll();
        AddCovalenceCommand(_pluginConfig.Command, nameof(SendWipeCommand));

        _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
        _previousProtocol = _storedData.Protocol;
            
        permission.RegisterPermission(AdminPermission, this);
            
        _pluginConfig.ProtocolWebhook = UpdateWebhookUrl(_pluginConfig.ProtocolWebhook);
        _pluginConfig.WipeWebhook =  UpdateWebhookUrl(_pluginConfig.WipeWebhook);

        foreach (DiscordMessageConfig embed in _pluginConfig.WipeEmbeds)
        {
            if (!string.IsNullOrEmpty(embed.WebhookOverride) && embed.WebhookOverride != DefaultUrl)
            {
                embed.WebhookOverride = UpdateWebhookUrl(embed.WebhookOverride);
            }
        }
            
        foreach (DiscordMessageConfig embed in _pluginConfig.ProtocolEmbeds)
        {
            if (!string.IsNullOrEmpty(embed.WebhookOverride) && embed.WebhookOverride != DefaultUrl)
            {
                embed.WebhookOverride = UpdateWebhookUrl(embed.WebhookOverride);
            }
        }

#if RUST
        _rustMapGenerateHeaders["X-API-Key"] = _pluginConfig.ImageSettings.RustMaps.ApiKey;
        _rustMapGetHeaders["X-API-Key"] = _pluginConfig.ImageSettings.RustMaps.ApiKey;
#endif
            
    }

    public string UpdateWebhookUrl(string url)
    {
        return url.Replace("/api/webhooks", "/api/v10/webhooks").Replace("https://discordapp.com/", "https://discord.com/");
    }
        
    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.NoPermission] = "You do not have permission to use this command",
            [LangKeys.SentWipe] = "You have sent a test wipe message",
            [LangKeys.SentProtocol] = "You have sent a test protocol message",
            [LangKeys.Help] = "Sends test message for plugin\n" +
                              "{0}{1} wipe - sends a wipe test message\n" +
                              "{0}{1} protocol - sends a protocol test message\n" +
                              "{0}{1} - displays this help text again" ,
        }, this);
    }
        
    protected override void LoadDefaultConfig()
    {
        PrintWarning("Loading Default Config");
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();
        Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
        _pluginConfig = AdditionalConfig(Config.ReadObject<PluginConfig>());
        Config.WriteObject(_pluginConfig);
    }

    private PluginConfig AdditionalConfig(PluginConfig config)
    {
        config.WipeEmbeds ??= new List<DiscordMessageConfig> { 
            new()
            {
                Content = "@everyone",
                SendMode = SendMode.Always,
                Embed = new EmbedConfig
                {
                    Title = "{server.name}",
                    Description = "The server has wiped!",
                    Url = string.Empty,
                    Color = "#de8732",
                    Image = MapAttachment,
                    Thumbnail = string.Empty,
                    Fields = new List<FieldConfig>
                    {
#if RUST
                        new()
                        {
                            Title = "Seed",
                            Value = "[{world.seed}](https://rustmaps.com/map/{world.size}_{world.seed})",
                            Inline = true,
                            Enabled = true
                        },
                        new()
                        {
                            Title = "Size",
                            Value = "{world.size}M ({world.size!km^2}km^2)",
                            Inline = true,
                            Enabled = true
                        },
                        new()
                        {
                            Title = "Protocol",
                            Value = "{server.protocol.network}",
                            Inline = true,
                            Enabled = true
                        },
#endif
                        new()
                        {
                            Title = "Click & Connect",
                            Value = "steam://connect/{server.address}:{server.port}",
                            Inline = false,
                            Enabled = true
                        }
                    },
                    Footer = new FooterConfig
                    {
                        IconUrl = string.Empty,
                        Text = string.Empty,
                        Enabled = true
                    },
                    Enabled = true
                }
            }};
            
        config.ProtocolEmbeds ??= new List<DiscordMessageConfig> { 
            new()
            {
                Content = "@everyone",
                SendMode = SendMode.Always,
                Embed = new EmbedConfig
                {
                    Title = "{server.name}",
                    Description = "The server protocol has changed!",
                    Url = string.Empty,
                    Color = "#de8732",
                    Image = string.Empty,
                    Thumbnail = string.Empty,
                    Fields = new List<FieldConfig>
                    {
                        new()
                        {
                            Title = "Protocol",
                            Value = "{server.protocol.network}",
                            Inline = true,
                            Enabled = true
                        },
                        new()
                        {
                            Title = "Previous Protocol",
                            Value = "{server.protocol.previous}",
                            Inline = true,
                            Enabled = true
                        },
                        new()
                        {
                            Title = "Mandatory Client Update",
                            Value = "This update requires a mandatory client update in order to be able to play on the server",
                            Inline = false,
                            Enabled = true
                        },
                        new()
                        {
                            Title = "Click & Connect",
                            Value = "steam://connect/{server.address}:{server.port}",
                            Inline = false,
                            Enabled = true
                        }
                    },
                    Footer = new FooterConfig
                    {
                        IconUrl = string.Empty,
                        Text = string.Empty,
                        Enabled = true
                    },
                    Enabled = true
                }
            }};

#if RUST
        config.ImageSettings = new RustMapImageSettings
        {
            MapMode = config.ImageSettings?.MapMode ?? (RustMapApi != null ? RustMapMode.RustMapApi : RustMapMode.None),
            RustMapApi = new RustMapApiSettings
            {
                Name = config.ImageSettings?.RustMapApi?.Name ?? "Icons",
                Scale = config.ImageSettings?.RustMapApi?.Scale ?? 0.5f,
                FileType = config.ImageSettings?.RustMapApi?.FileType ?? EncodingMode.Jpg
            },
            RustMaps = new RustMapSettings
            {
                ApiKey = config.ImageSettings?.RustMaps?.ApiKey ?? "Get Your API Key @ https://rustmaps.com/user/profile",
                Staging = config.ImageSettings?.RustMaps?.Staging ?? false
            }
        };
#endif

        foreach (DiscordMessageConfig embed in config.WipeEmbeds)
        {
            if (string.IsNullOrEmpty(embed.WebhookOverride))
            {
                embed.WebhookOverride = DefaultUrl;
            }
        }
            
        foreach (DiscordMessageConfig embed in config.ProtocolEmbeds)
        {
            if (string.IsNullOrEmpty(embed.WebhookOverride))
            {
                embed.WebhookOverride = DefaultUrl;
            }
        }

        return config;
    }
        
    private void OnServerInitialized()
    {
        _protocol = GetProtocol();
        if (PlaceholderAPI is not { IsLoaded: true })
        {
            PrintError("Missing plugin dependency PlaceholderAPI: https://umod.org/plugins/placeholder-api");
            return;
        }
            
        if(PlaceholderAPI.Version < new VersionNumber(2, 2, 0))
        {
            PrintError("Placeholder API plugin must be version 2.2.0 or higher");
            return;
        }

#if RUST
        if (IsRustMapApiLoaded() && _pluginConfig.ImageSettings.MapMode == RustMapMode.RustMapApi)
        {
            if(RustMapApi.Version < new VersionNumber(1,3,2))
            {
                PrintError("RustMapApi plugin must be version 1.3.2 or higher");
                return;
            }
                
            if (!IsRustMapApiReady())
            {
                Debug(DebugEnum.Info, "Waiting for Rust Maps Api Plugin to be Ready");
                SubscribeAll();
                return;
            }
        }

        if (_pluginConfig.ImageSettings.MapMode == RustMapMode.RustMaps && !string.IsNullOrEmpty(_pluginConfig.ImageSettings.RustMaps.ApiKey) && _pluginConfig.ImageSettings.RustMaps.ApiKey != DefaultRustMapsApiKey)
        {
            Debug(DebugEnum.Info, "Loading Map from RustMaps.com");
            GetRustMapsMap();
            //timer.In(15 * 60f, HandleStartup);
            return;
        }
#endif

        //Delayed so PlaceholderAPI can be ready before we call
        timer.In(1f, () => HandleStartup("OnServerInit"));
    }

    private void OnRustMapApiReady()
    {
        HandleStartup("OnRustMapApiReady");
    }
        
    private void HandleStartup(string source)
    {
        Debug(DebugEnum.Info, $"HandleStartup - {source}");
        if (_hasStarted)
        {
            Debug(DebugEnum.Info, "HandleStartup - Skipping as already started");
            return;
        }

        _hasStarted = true;
            
        if (string.IsNullOrEmpty(_storedData.Protocol))
        {
            Debug(DebugEnum.Info, $"HandleStartup - Protocol is not set setting protocol to: {_protocol}");
            _storedData.Protocol = _protocol;
            SaveData();
        }
        else if (_storedData.Protocol != _protocol)
        {
            Debug(DebugEnum.Info, $"HandleStartup - Protocol has changed {_storedData.Protocol} -> {_protocol}");
            if (_pluginConfig.SendProtocolAutomatically)
            {
                SendProtocol();
            }
            _storedData.Protocol = _protocol;
            SaveData();
            Puts("Protocol notification sent");
        }
        else
        {
            Debug(DebugEnum.Info, "HandleStartup - Protocol has not changed");
        }
            
        if (_storedData.IsWipe)
        {
            if (_pluginConfig.SendWipeAutomatically)
            {
                Debug(DebugEnum.Info, "HandleStartup - IsWipe is set. Sending wipe message.");
                SendWipe();
                Puts("Wipe notification sent");
            }
            else
            {
                Debug(DebugEnum.Info, "SendWipeAutomatically is disabled");
            }
            _storedData.IsWipe = false;
            SaveData();
        }
        else
        {
            Debug(DebugEnum.Info, "HandleStartup - Not a wipe");
        }
    }

    private void OnNewSave()
    {
        _storedData.IsWipe = true;
        Debug(DebugEnum.Info, "OnNewSave - Wipe Detected");
        SaveData();
    }

    private void Unload()
    {
        SaveData();
    }
        
    private string GetProtocol()
    {
#if RUST
        return Rust.Protocol.network.ToString();
#else 
            return covalence.Server.Protocol;
#endif
    }
    #endregion

    #region Command
    private bool SendWipeCommand(IPlayer player, string cmd, string[] args)
    {
        if (!HasPermission(player, AdminPermission))
        {
            player.Message(Lang(LangKeys.NoPermission, player));
            return true;
        }

        Debug(DebugEnum.Info, $"SendWipeCommand command called: {string.Join(" ", args)}");
            
        string commandPrefix = player.IsServer ? "" : "/";
        if (args.Length == 0)
        {
            player.Message(Lang(LangKeys.Help, player, commandPrefix, _pluginConfig.Command));
            return true;
        }
            
        switch (args[0].ToLower())
        {
            case "wipe":
                SendWipe();
                player.Message(Lang(LangKeys.SentWipe, player));
                break;
                
            case "protocol":
                SendProtocol();
                player.Message(Lang(LangKeys.SentProtocol, player));
                break;
                
            default:
                player.Message(Lang(LangKeys.Help, player,commandPrefix, _pluginConfig.Command));
                break;
        }
            
        return true;
    }
    #endregion

    #region Message Handling
    private void SendWipe()
    {
        Debug(DebugEnum.Info, "SendWipe - Sending wipe message");
        if (string.IsNullOrEmpty(_pluginConfig.WipeWebhook) || _pluginConfig.WipeWebhook == DefaultUrl)
        {
            Debug(DebugEnum.Info, "SendWipe - Wipe message not sent due to Wipe Webhook being blank or matching the default url");
            return;
        }

        List<DiscordMessageConfig> messages = _pluginConfig.WipeEmbeds
            .Where(w => w.SendMode == SendMode.Always)
            .ToList();
            
        DiscordMessageConfig random = _pluginConfig.WipeEmbeds
            .Where(w => w.SendMode == SendMode.Random)
            .OrderBy(w => Guid.NewGuid())
            .FirstOrDefault();

        if (random != null)
        {
            messages.Add(random);
        }

        SendMessage(_pluginConfig.WipeWebhook, messages);
        Debug(DebugEnum.Message, "");
    }

    private void SendProtocol()
    {
        Debug(DebugEnum.Info, "SendProtocol - Sending protocol message");
        if (string.IsNullOrEmpty(_pluginConfig.ProtocolWebhook) || _pluginConfig.ProtocolWebhook == DefaultUrl)
        {
            Debug(DebugEnum.Info, "SendProtocol - Protocol message not sent due to Protocol Webhook being blank or matching the default url");
            return;
        }
            
        List<DiscordMessageConfig> messages = _pluginConfig.ProtocolEmbeds
            .Where(w => w.SendMode == SendMode.Always)
            .ToList();
            
        DiscordMessageConfig random = _pluginConfig.ProtocolEmbeds
            .Where(w => w.SendMode == SendMode.Random)
            .OrderBy(w => Guid.NewGuid())
            .FirstOrDefault();

        if (random != null)
        {
            messages.Add(random);
        }
            
        SendMessage(_pluginConfig.ProtocolWebhook, messages);
    }

    private void SendMessage(string url, List<DiscordMessageConfig> messageConfigs)
    {
        for (int index = 0; index < messageConfigs.Count; index++)
        {
            DiscordMessageConfig messageConfig = messageConfigs[index];
            DiscordMessage message = ParseMessage(messageConfig);

            timer.In(index + 1, () =>
            {
#if RUST
                List<Attachment> attachments = new();

                Debug(DebugEnum.Info, $"SendMessage - MapMode={_pluginConfig.ImageSettings.MapMode.ToString()}");
                if (_pluginConfig.ImageSettings.MapMode == RustMapMode.RustMapApi)
                {
                    AttachMap(attachments, messageConfig);
                }

                if (!string.IsNullOrEmpty(messageConfig.WebhookOverride) && messageConfig.WebhookOverride != DefaultUrl)
                {
                    url = messageConfig.WebhookOverride;
                }
                    
                SendDiscordAttachmentMessage(url, message, attachments);
#else
                SendDiscordMessage(url, message);
#endif
            });
        }
    }
    #endregion
        
#if RUST
    private void AttachMap(List<Attachment> attachments, DiscordMessageConfig messageConfig)
    {
        Debug(DebugEnum.Info, $"Can attach map? RustMapApi Loaded: {IsRustMapApiLoaded()} RustMapApiReady: {IsRustMapApiReady()} Attachment Base:{messageConfig.Embed.Image.StartsWith(AttachmentBase)}");
        if (IsRustMapApiLoaded() && IsRustMapApiReady() && messageConfig.Embed.Image.StartsWith(AttachmentBase))
        {
            Debug(DebugEnum.Info, "AttachMap - RustMapApi is ready, attaching map");
            List<string> maps = RustMapApi.Call<List<string>>("GetSavedMaps");
            string mapName = _pluginConfig.ImageSettings.RustMapApi.Name;
            if (maps != null)
            {
                mapName = maps.FirstOrDefault(m => m.Equals(mapName, StringComparison.InvariantCultureIgnoreCase));
                if (string.IsNullOrEmpty(mapName))
                {
                    PrintWarning($"Map name not found {_pluginConfig.ImageSettings.RustMapApi.Name}. Valid names are {string.Join(", ", maps.ToArray())}");
                    mapName = "Icons";
                }
            }
                
            Debug(DebugEnum.Info, $"AttachMap - RustMapApi map name set to: {mapName}");
            int resolution = (int) (World.Size * _pluginConfig.ImageSettings.RustMapApi.Scale);
            AttachmentContentType contentType = _pluginConfig.ImageSettings.RustMapApi.FileType == EncodingMode.Jpg ? AttachmentContentType.Jpg : AttachmentContentType.Png; 
            object response = RustMapApi.Call("CreatePluginImage", this, mapName, resolution, (int)_pluginConfig.ImageSettings.RustMapApi.FileType);
            if (response is string)
            {
                PrintError($"An error occurred creating the plugin image: {response}");
                return;
            }

            Hash<string, object> map = response as Hash<string, object>;
            if (map?["image"] is byte[] mapData)
            {
                if (mapData.Length >= 8 * 1024 * 1024)
                {
                    PrintError( "Map Image too large. " +
                                $"Image size is {mapData.Length / 1024.0 / 1024.0:0.00}MB. " +
                                $"Max size is {MaxImageSize / 1024 / 1024}MB. " +
                                "Please reduce the \"Image Resolution Scale\"");
                }
                else
                {
                    attachments.Add(new Attachment(mapData, MapFilename, contentType));
                    Debug(DebugEnum.Info, "AttachMap - Successfully attached map");
                }
                    
            }
            else
            {
                Debug(DebugEnum.Warning, "AttachMap - MapData was null!!!");
            }
        }
    }

    #region RustMaps.com
    private readonly Dictionary<string, string> _rustMapGenerateHeaders = new()
    {
        ["Content-Type"] = "application/json",
    };
        
    private readonly Dictionary<string, string> _rustMapGetHeaders = new();

    public void GetRustMapsMap()
    {
        Debug(DebugEnum.Info, $"{nameof(GetRustMapsMap)} - Convar.Server.levelurl: \"{ConVar.Server.levelurl}\"");
        if (string.IsNullOrEmpty(ConVar.Server.levelurl) || (ConVar.Server.levelurl.StartsWith("https://files.facepunch.com/rust/maps") && ConVar.Server.levelurl.Contains("proceduralmap.")))
        {
            uint seed = World.Seed;
            uint size = World.Size;
            
            string url = $"https://api.rustmaps.com/v4/maps/{size}/{seed}?staging={_pluginConfig.ImageSettings.RustMaps.Staging}";
            Debug(DebugEnum.Info, $"RustMaps.com Requesting Rust Map. Url: {url}");
            webrequest.Enqueue(url, null, (code, response) => RustMapsGetCallback(code, response, false), this, RequestMethod.GET, _rustMapGetHeaders);
        }
        else if(ConVar.Server.levelurl.StartsWith("https://maps.rustmaps.com"))
        {
            Regex regex = new(@"https:\/\/maps\.rustmaps\.com\/\d*\/([a-z\d]*)\/");
            string match = regex.Match(ConVar.Server.levelurl).Groups[1].Value;
            if (!string.IsNullOrEmpty(match))
            {
                string url = $"https://api.rustmaps.com/v4/maps/{match}";
                Debug(DebugEnum.Info, $"RustMaps.com Requesting Rust Map. Url: {url}");
                webrequest.Enqueue(url, null, (code, response) => RustMapsGetCallback(code, response, true), this, RequestMethod.GET, _rustMapGetHeaders);
            }
        }
    }

    private void RustMapsGetCallback(int code, string response, bool isCustom)
    {
        if (code == 409)
        {
            Debug(DebugEnum.Message, "RustMaps.com is still generating the map image. Trying again in 60 seconds.");
            timer.In(60f, GetRustMapsMap);
        }
        else if (!isCustom && code == 404)
        {
            Debug(DebugEnum.Message, $"RustMaps.com map does not exist. Requesting Generation.\n{response}");
            RustMapsRequestMap();
        }
        else if (code == 200)
        {
            Debug(DebugEnum.Info, "RustMaps.com map image found.");
            _rustMapsResponse = JsonConvert.DeserializeObject<RustMapsResponse>(response);
            HandleStartup("RustMapsGetCallback");
        }
        else
        {
            Debug(DebugEnum.Error,$"An error occured trying to get map image from RustMaps.com - Code:{code} Response:\n{response}");
            timer.In(60f, GetRustMapsMap);
        }
    }

    public void RustMapsRequestMap()
    {
        Debug(DebugEnum.Info, "RustMaps.com Generating Rust Map.");
        RustMapsApiRequest request = new()
        {
            Size = World.Size,
            Seed = World.Seed,
            Staging = _pluginConfig.ImageSettings.RustMaps.Staging
        };

        string json = JsonConvert.SerializeObject(request);
        Debug(DebugEnum.Info, json);
            
        webrequest.Enqueue("https://api.rustmaps.com/v4/maps", json, RustMapsPostCallback, this, RequestMethod.POST, _rustMapGenerateHeaders);
    }

    private void RustMapsPostCallback(int code, string response)
    {
        if (code == 201)
        {
            Debug(DebugEnum.Info, "RustMaps.com image requested successfully.");
            timer.In(60f, GetRustMapsMap);
        }
        else if (code == 409)
        {
            Debug(DebugEnum.Info, "RustMaps.com map already exists. Requesting image.");
            GetRustMapsMap();
        }
        else
        {
            Debug(DebugEnum.Error,$"An error occured trying to request map generation from RustMaps.com - Code:{code} Response:\n{response}");
            timer.In(60f, GetRustMapsMap);
        }
    }
    #endregion
#endif
        
    #region PlaceholderAPI
    private string ParseField(string field)
    {
        _parser.Length = 0;
        _parser.Append(field);
        GetReplacer()?.Invoke(null, _parser, false);
        return _parser.ToString();
    }
        
    private void OnPluginUnloaded(Plugin plugin)
    {
        if (plugin?.Name == "PlaceholderAPI")
        {
            _replacer = null;
        }
    }
        
    private void OnPlaceholderAPIReady()
    {
        RegisterPlaceholder("server.protocol.previous", (player, s) => _previousProtocol, "Displays the previous protocol version if it changed during the last restart", double.MaxValue);
        RegisterPlaceholder("timestamp.now", (player, s) => UnixTimeNow(), "Displays the current unix timestamp", double.MaxValue);
#if RUST
        RegisterPlaceholder("rustmaps.com.map", (player, s) => _rustMapsResponse?.Data?.ImageUrl ?? string.Empty, "RustMaps.com map image url", double.MaxValue);
        RegisterPlaceholder("rustmaps.com.icons", (player, s) => _rustMapsResponse?.Data?.ImageIconUrl ?? string.Empty, "RustMaps.com icon map image url", double.MaxValue);
        RegisterPlaceholder("rustmaps.com.thumbnail", (player, s) => _rustMapsResponse?.Data?.ThumbnailUrl ?? string.Empty, "RustMaps.com thumbnail map image url", double.MaxValue);
#endif
    }

    private void RegisterPlaceholder(string key, Func<IPlayer, string, object> action, string description = null, double ttl = double.NaN)
    {
        if (IsPlaceholderApiLoaded())
        {
            PlaceholderAPI.Call("AddPlaceholder", this, key, action, description, ttl);
        }
    }
        
    private Action<IPlayer, StringBuilder, bool> GetReplacer()
    {
        if (!IsPlaceholderApiLoaded())
        {
            return _replacer;
        }
            
        return _replacer ?? (_replacer = PlaceholderAPI.Call<Action<IPlayer, StringBuilder, bool>>("GetProcessPlaceholders", 1));
    }

    private bool IsPlaceholderApiLoaded() => PlaceholderAPI != null && PlaceholderAPI.IsLoaded;
    #endregion

    #region Helpers
    public long UnixTimeNow()
    {
        TimeSpan timeSpan = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0);
        return (long)timeSpan.TotalSeconds;
    }
        
    public void UnsubscribeAll()
    {
        Unsubscribe(nameof(OnRustMapApiReady));
    }

    public void SubscribeAll()
    {
        Subscribe(nameof(OnRustMapApiReady));
    }
        
    private void Debug(DebugEnum level, string message)
    {
        if (level > _pluginConfig.DebugLevel)
        {
            return;
        }

        switch (level)
        {
            case DebugEnum.Error:
                PrintError(message);
                break;
            case DebugEnum.Warning:
                PrintWarning(message);
                break;
            default:
                Puts($"{level}: {message}");
                break;
        }
    }

    private string Lang(string key, IPlayer player = null, params object[] args)
    {
        try
        {
            return string.Format(lang.GetMessage(key, this, player?.Id), args);
        }
        catch(Exception ex)
        {
            PrintError($"Lang Key '{key}' threw exception\n:{ex.Message}");
            throw;
        }
    }

    private bool IsRustMapApiLoaded() => RustMapApi is { IsLoaded: true };
    private bool IsRustMapApiReady() => RustMapApi.Call<bool>("IsReady");

    private void SaveData()
    {
        if (_storedData != null)
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
        }
    }

    private bool HasPermission(IPlayer player, string perm) => permission.UserHasPermission(player.Id, perm);
    #endregion

    #region Classes
    private class PluginConfig
    {
        [JsonConverter(typeof(StringEnumConverter))]
        [DefaultValue(DebugEnum.Warning)]
        [JsonProperty(PropertyName = "Debug Level (None, Error, Warning, Info)")]
        public DebugEnum DebugLevel { get; set; }
            
        [DefaultValue("dw")]
        [JsonProperty(PropertyName = "Command")]
        public string Command { get; set; }
            
#if RUST
        [JsonProperty(PropertyName = "Rust Map Image Settings")]
        public RustMapImageSettings ImageSettings { get; set; }
#endif
            
        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Send wipe message when server wipes")]
        public bool SendWipeAutomatically { get; set; }
            
        [DefaultValue(DefaultUrl)]
        [JsonProperty(PropertyName = "Wipe Webhook url")]
        public string WipeWebhook { get; set; }
            
        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Send protocol message when server protocol changes")]
        public bool SendProtocolAutomatically { get; set; }
            
        [DefaultValue(DefaultUrl)]
        [JsonProperty(PropertyName = "Protocol Webhook url")]
        public string ProtocolWebhook { get; set; }
            
        [JsonProperty(PropertyName = "Wipe messages")]
        public List<DiscordMessageConfig> WipeEmbeds { get; set; }
            
        [JsonProperty(PropertyName = "Protocol messages")]
        public List<DiscordMessageConfig> ProtocolEmbeds { get; set; }
    }
        
    public class RustMapsApiRequest
    {
        [JsonProperty("size")]
        public uint Size { get; set; }

        [JsonProperty("seed")]
        public uint Seed { get; set; }

        [JsonProperty("staging")]
        public bool Staging { get; set; }

        [JsonProperty("barren")]
        public bool Barren { get; set; }
    }


#if RUST
    private class RustMapImageSettings
    {
        [JsonConverter(typeof(StringEnumConverter))]
        [JsonProperty(PropertyName = "Map Image Source (None, RustMaps, RustMapApi)")]
        public RustMapMode MapMode { get; set; }
            
        [JsonProperty(PropertyName = "RustMaps.com Settings")]
        public RustMapSettings RustMaps { get; set; }
            
        [JsonProperty(PropertyName = "RustMapApi Settings")]
        public RustMapApiSettings RustMapApi { get; set; }
    }

    private class RustMapSettings
    {
        [JsonProperty(PropertyName = "RustMap.com API Key")]
        public string ApiKey { get; set; }
            
        [JsonProperty(PropertyName = "Generate Staging Map")]
        public bool Staging { get; set; }
    }

    private class RustMapApiSettings
    {
        [DefaultValue("Icons")]
        [JsonProperty(PropertyName = "Render Name")]
        public string Name { get; set; }
            
        [DefaultValue(0.5f)]
        [JsonProperty(PropertyName = "Image Resolution Scale")]
        public float Scale { get; set; }            
            
        [JsonConverter(typeof(StringEnumConverter))]
        [DefaultValue(EncodingMode.Jpg)]
        [JsonProperty(PropertyName = "File Type (Jpg, Png")]
        public EncodingMode FileType { get; set; }
    }

    public class RustMapsResponse
    {
        [JsonProperty("data")]
        public RustMapsData Data { get; set; }
    }
        
    public class RustMapsData
    {
        [JsonProperty("imageUrl")]
        public string ImageUrl { get; set; }
            
        [JsonProperty("imageIconUrl")]
        public string ImageIconUrl { get; set; }
            
        [JsonProperty("thumbnailUrl")]
        public string ThumbnailUrl { get; set; }
    }
#endif
        
    private class StoredData
    {
        public bool IsWipe { get; set; }
        public string Protocol { get; set; }
    }

    private class LangKeys
    {
        public const string NoPermission = "NoPermission";
        public const string SentWipe = "SentWipe";
        public const string SentProtocol = "SentProtocol";
        public const string Help = "Help";
    }
    #endregion
        
    #region Discord Embed
    #region Send Embed Methods
    /// <summary>
    /// Headers when sending an embeded message
    /// </summary>
    private readonly Dictionary<string, string> _headers = new()
    {
        {"Content-Type", "application/json"}
    };

    /// <summary>
    /// Sends the DiscordMessage to the specified webhook url
    /// </summary>
    /// <param name="url">Webhook url</param>
    /// <param name="message">Message being sent</param>
    private void SendDiscordMessage(string url, DiscordMessage message)
    {
        StringBuilder json = message.ToJson();
        if (_pluginConfig.DebugLevel >= DebugEnum.Info)
        {
            Debug(DebugEnum.Info, $"{nameof(SendDiscordMessage)} message.ToJson()\n{json}");
        }
            
        webrequest.Enqueue(url, json.ToString(), SendDiscordMessageCallback, this, RequestMethod.POST, _headers);
    }

    /// <summary>
    /// Callback when sending the embed if any errors occured
    /// </summary>
    /// <param name="code">HTTP response code</param>
    /// <param name="message">Response message</param>
    private void SendDiscordMessageCallback(int code, string message)
    {
        if (code != 204)
        {
            PrintError(message);
        }
    }

#if RUST
    /// <summary>
    /// Sends the DiscordMessage to the specified webhook url with attachments
    /// </summary>
    /// <param name="url">Webhook url</param>
    /// <param name="message">Message being sent</param>
    /// <param name="files">Attachments to be added to the DiscordMessage</param>
    private void SendDiscordAttachmentMessage(string url, DiscordMessage message, List<Attachment> files)
    {
        StringBuilder json = message.ToJson();
        if (_pluginConfig.DebugLevel >= DebugEnum.Info)
        {
            Debug(DebugEnum.Info, $"{nameof(SendDiscordAttachmentMessage)} message.ToJson()\n{json}");
        }

        List<IMultipartFormSection> formData = new()
        {
            new MultipartFormDataSection("payload_json", json.ToString())
        };

        for (int i = 0; i < files.Count; i++)
        {
            Attachment attachment = files[i];
            formData.Add(new MultipartFormFileSection($"file{i + 1}", attachment.Data, attachment.Filename, attachment.ContentType));
        }

        InvokeHandler.Instance.StartCoroutine(SendDiscordAttachmentMessageHandler(url, formData));
    }

    private IEnumerator SendDiscordAttachmentMessageHandler(string url, List<IMultipartFormSection> data)
    {
        UnityWebRequest www = UnityWebRequest.Post(url, data);
        yield return www.SendWebRequest();

        if (www.isNetworkError || www.isHttpError)
        {
            PrintError($"CODE: {www.error} ERROR: {www.downloadHandler.text}");
        }
    }
#endif
    #endregion
        
    #region Helper Methods

    private const string OwnerIcon = "https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/47/47db946f27bc76d930ac82f1656f7a10707bb67d_full.jpg";

    private void AddPluginInfoFooter(Embed embed)
    {
        embed.AddFooter($"{Title} V{Version} by {Author}", OwnerIcon);
    }

    private string GetPositionField(Vector3 pos)
    {
        return $"{pos.x:0.00} {pos.y:0.00} {pos.z:0.00}";
    }
    #endregion
        
    #region Embed Classes

    private class DiscordMessage
    {
        /// <summary>
        /// The name of the user sending the message changing this will change the webhook bots name
        /// </summary>
        [JsonProperty("username")]
        private string Username { get; set; }

        /// <summary>
        /// The avatar url of the user sending the message changing this will change the webhook bots avatar
        /// </summary>
        [JsonProperty("avatar_url")]
        private string AvatarUrl { get; set; }

        /// <summary>
        /// String only content to be sent
        /// </summary>
        [JsonProperty("content")]
        private string Content { get; set; }

        /// <summary>
        /// Embeds to be sent
        /// </summary>
        [JsonProperty("embeds")]
        private List<Embed> Embeds { get; }

        public DiscordMessage(string username = null, string avatarUrl = null)
        {
            Username = username;
            AvatarUrl = avatarUrl;
            Embeds = new List<Embed>();
        }

        public DiscordMessage(string content, string username = null, string avatarUrl = null)
        {
            Content = content;
            Username = username;
            AvatarUrl = avatarUrl;
            Embeds = new List<Embed>();
        }

        public DiscordMessage(Embed embed, string username = null, string avatarUrl = null)
        {
            Embeds = new List<Embed> {embed};
            Username = username;
            AvatarUrl = avatarUrl;
        }

        /// <summary>
        /// Adds a new embed to the list of embed to send
        /// </summary>
        /// <param name="embed">Embed to add</param>
        /// <returns>This</returns>
        /// <exception cref="IndexOutOfRangeException">Thrown if more than 10 embeds are added in a send as that is the discord limit</exception>
        public DiscordMessage AddEmbed(Embed embed)
        {
            if (Embeds.Count >= 10)
            {
                throw new IndexOutOfRangeException("Only 10 embed are allowed per message");
            }

            Embeds.Add(embed);
            return this;
        }

        /// <summary>
        /// Adds string content to the message
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        public DiscordMessage AddContent(string content)
        {
            Content = content;
            return this;
        }

        /// <summary>
        /// Changes the username and avatar image for the bot sending the message
        /// </summary>
        /// <param name="username">username to change</param>
        /// <param name="avatarUrl">avatar img url to change</param>
        /// <returns>This</returns>
        public DiscordMessage AddSender(string username, string avatarUrl)
        {
            Username = username;
            AvatarUrl = avatarUrl;
            return this;
        }

        /// <summary>
        /// Returns message as JSON to be sent in the web request
        /// </summary>
        /// <returns></returns>
        public StringBuilder ToJson() => new(JsonConvert.SerializeObject(this, Formatting.None,
            new JsonSerializerSettings {NullValueHandling = NullValueHandling.Ignore}));
    }

    private class Embed
    {
        /// <summary>
        /// Color of the left side bar of the embed message
        /// </summary>
        [JsonProperty("color")]
        private int Color { get; set; }

        /// <summary>
        /// Fields to be added to the embed message
        /// </summary>
        [JsonProperty("fields")]
        private List<Field> Fields { get; } = new();

        /// <summary>
        /// Title of the embed message
        /// </summary>
        [JsonProperty("title")]
        private string Title { get; set; }

        /// <summary>
        /// Description of the embed message
        /// </summary>
        [JsonProperty("description")]
        private string Description { get; set; }
            
        /// <summary>
        /// Description of the embed message
        /// </summary>
        [JsonProperty("url")]
        private string Url { get; set; }

        /// <summary>
        /// Image to added to the embed message. Appears at the bottom of the message above the footer
        /// </summary>
        [JsonProperty("image")]
        private Image Image { get; set; }

        /// <summary>
        /// Thumbnail image added to the embed message. Appears in the top right corner
        /// </summary>
        [JsonProperty("thumbnail")]
        private Image Thumbnail { get; set; }

        /// <summary>
        /// Video to add to the embed message
        /// </summary>
        [JsonProperty("video")]
        private Video Video { get; set; }

        /// <summary>
        /// Author to add to the embed message. Appears above the title.
        /// </summary>
        [JsonProperty("author")]
        private AuthorInfo Author { get; set; }

        /// <summary>
        /// Footer to add to the embed message. Appears below all content.
        /// </summary>
        [JsonProperty("footer")]
        private Footer Footer { get; set; }

        /// <summary>
        /// Adds a title to the embed message
        /// </summary>
        /// <param name="title">Title to add</param>
        /// <returns>This</returns>
        public Embed AddTitle(string title)
        {
            Title = title;
            return this;
        }

        /// <summary>
        /// Adds a description to the embed message
        /// </summary>
        /// <param name="description">description to add</param>
        /// <returns>This</returns>
        public Embed AddDescription(string description)
        {
            Description = description;
            return this;
        }

        /// <summary>
        /// Adds a url to the embed message
        /// </summary>
        /// <param name="url"></param>
        /// <returns>This</returns>
        public Embed AddUrl(string url)
        {
            Url = url;
            return this;
        }

        /// <summary>
        /// Adds an author to the embed message. The author will appear above the title
        /// </summary>
        /// <param name="name">Name of the author</param>
        /// <param name="iconUrl">Icon Url to use for the author</param>
        /// <param name="url">Url to go to when the authors name is clicked on</param>
        /// <param name="proxyIconUrl">Backup icon url. Can be left null if you only have one icon url</param>
        /// <returns>This</returns>
        public Embed AddAuthor(string name, string iconUrl = null, string url = null, string proxyIconUrl = null)
        {
            Author = new AuthorInfo(name, iconUrl, url, proxyIconUrl);
            return this;
        }

        /// <summary>
        /// Adds a footer to the embed message
        /// </summary>
        /// <param name="text">Text to be added to the footer</param>
        /// <param name="iconUrl">Icon url to add in the footer. Appears to the left of the text</param>
        /// <param name="proxyIconUrl">Backup icon url. Can be left null if you only have one icon url</param>
        /// <returns>This</returns>
        public Embed AddFooter(string text, string iconUrl = null, string proxyIconUrl = null)
        {
            Footer = new Footer(text, iconUrl, proxyIconUrl);

            return this;
        }

        /// <summary>
        /// Adds an int based color to the embed. Color appears as a bar on the left side of the message
        /// </summary>
        /// <param name="color"></param>
        /// <returns></returns>
        public Embed AddColor(int color)
        {
            if (color < 0x0 || color > 0xFFFFFF)
            {
                throw new Exception($"Color '{color}' is outside the valid color range");
            }
                
            Color = color;
            return this;
        }

        /// <summary>
        /// Adds a hex based color. Color appears as a bar on the left side of the message
        /// </summary>
        /// <param name="color">Color in string hex format</param>
        /// <returns>This</returns>
        /// <exception cref="Exception">Exception thrown if color is outside of range</exception>
        public Embed AddColor(string color)
        {
            int parsedColor = int.Parse(color.TrimStart('#'), NumberStyles.AllowHexSpecifier);
            if (parsedColor < 0x0 || parsedColor > 0xFFFFFF)
            {
                throw new Exception($"Color '{color}' is outside the valid color range");
            }

            Color = parsedColor;
            return this;
        }

        /// <summary>
        /// Adds a RGB based color. Color appears as a bar on the left side of the message
        /// </summary>
        /// <param name="red">Red value between 0 - 255</param>
        /// <param name="green">Green value between 0 - 255</param>
        /// <param name="blue">Blue value between 0 - 255</param>
        /// <returns>This</returns>
        /// <exception cref="Exception">Thrown if red, green, or blue is outside of range</exception>
        public Embed AddColor(int red, int green, int blue)
        {
            if (red < 0 || red > 255 || green < 0 || green > 255 || green < 0 || green > 255)
            {
                throw new Exception($"Color Red:{red} Green:{green} Blue:{blue} is outside the valid color range. Must be between 0 - 255");
            }

            Color = red * 65536 + green * 256 + blue;;
            return this;
        }

        /// <summary>
        /// Adds a blank field.
        /// If inline it will add a blank column.
        /// If not inline will add a blank row
        /// </summary>
        /// <param name="inline">If the field is inline</param>
        /// <returns>This</returns>
        public Embed AddBlankField(bool inline)
        {
            Fields.Add(new Field("\u200b", "\u200b", inline));
            return this;
        }

        /// <summary>
        /// Adds a new field with the name as the title and value as the value.
        /// If inline will add a new column. If row will add in a new row.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <param name="inline"></param>
        /// <returns></returns>
        public Embed AddField(string name, string value, bool inline)
        {
            Fields.Add(new Field(name, value, inline));
            return this;
        }

        /// <summary>
        /// Adds an image to the embed. The url should point to the url of the image.
        /// If using attachment image you can make the url: "attachment://{image name}.{image extension}
        /// </summary>
        /// <param name="url">Url for the image</param>
        /// <param name="width">width of the image</param>
        /// <param name="height">height of the image</param>
        /// <param name="proxyUrl">Backup url for the image</param>
        /// <returns></returns>
        public Embed AddImage(string url, int? width = null, int? height = null, string proxyUrl = null)
        {
            Image = new Image(url, width, height, proxyUrl);
            return this;
        }

        /// <summary>
        /// Adds a thumbnail in the top right corner of the embed
        /// If using attachment image you can make the url: "attachment://{image name}.{image extension}
        /// </summary>
        /// <param name="url">Url for the image</param>
        /// <param name="width">width of the image</param>
        /// <param name="height">height of the image</param>
        /// <param name="proxyUrl">Backup url for the image</param>
        /// <returns></returns>
        public Embed AddThumbnail(string url, int? width = null, int? height = null, string proxyUrl = null)
        {
            Thumbnail = new Image(url, width, height, proxyUrl);
            return this;
        }

        /// <summary>
        /// Adds a video to the embed
        /// </summary>
        /// <param name="url">Url for the video</param>
        /// <param name="width">Width of the video</param>
        /// <param name="height">Height of the video</param>
        /// <returns></returns>
        public Embed AddVideo(string url, int? width = null, int? height = null)
        {
            Video = new Video(url, width, height);
            return this;
        }
    }

    /// <summary>
    /// Field for and embed message
    /// </summary>
    private class Field
    {
        /// <summary>
        /// Name of the field
        /// </summary>
        [JsonProperty("name")]
        private string Name { get; }

        /// <summary>
        /// Value for the field
        /// </summary>
        [JsonProperty("value")]
        private string Value { get; }

        /// <summary>
        /// If the field should be in the same row or a new row
        /// </summary>
        [JsonProperty("inline")]
        private bool Inline { get; }

        public Field(string name, string value, bool inline)
        {
            Name = name;
            Value = value;
            Inline = inline;
        }
    }

    /// <summary>
    /// Image for an embed message
    /// </summary>
    private class Image
    {
        /// <summary>
        /// Url for the image
        /// </summary>
        [JsonProperty("url")]
        private string Url { get; }

        /// <summary>
        /// Width for the image
        /// </summary>
        [JsonProperty("width")]
        private int? Width { get; }

        /// <summary>
        /// Height for the image
        /// </summary>
        [JsonProperty("height")]
        private int? Height { get; }

        /// <summary>
        /// Proxy url for the image
        /// </summary>
        [JsonProperty("proxyURL")]
        private string ProxyUrl { get; }

        public Image(string url, int? width, int? height, string proxyUrl)
        {
            Url = url;
            Width = width;
            Height = height;
            ProxyUrl = proxyUrl;
        }
    }

    /// <summary>
    /// Video for an embed message
    /// </summary>
    private class Video
    {
        /// <summary>
        /// Url to the video
        /// </summary>
        [JsonProperty("url")]
        private string Url { get; }

        /// <summary>
        /// Width of the video
        /// </summary>
        [JsonProperty("width")]
        private int? Width { get; }

        /// <summary>
        /// Height of the video
        /// </summary>
        [JsonProperty("height")]
        private int? Height { get; }

        public Video(string url, int? width, int? height)
        {
            Url = url;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Author of an embed message
    /// </summary>
    private class AuthorInfo
    {
        /// <summary>
        /// Name of the author
        /// </summary>
        [JsonProperty("name")]
        private string Name { get; }

        /// <summary>
        /// Url to go to when clicking on the authors name
        /// </summary>
        [JsonProperty("url")]
        private string Url { get; }

        /// <summary>
        /// Icon url for the author
        /// </summary>
        [JsonProperty("icon_url")]
        private string IconUrl { get; }

        /// <summary>
        /// Proxy icon url for the author
        /// </summary>
        [JsonProperty("proxy_icon_url")]
        private string ProxyIconUrl { get; }

        public AuthorInfo(string name, string iconUrl, string url, string proxyIconUrl)
        {
            Name = name;
            Url = url;
            IconUrl = iconUrl;
            ProxyIconUrl = proxyIconUrl;
        }
    }

    /// <summary>
    /// Footer for an embed message
    /// </summary>
    private class Footer
    {
        /// <summary>
        /// Text for the footer
        /// </summary>
        [JsonProperty("text")]
        private string Text { get; }

        /// <summary>
        /// Icon url for the footer
        /// </summary>
        [JsonProperty("icon_url")]
        private string IconUrl { get; }

        /// <summary>
        /// Proxy icon url for the footer
        /// </summary>
        [JsonProperty("proxy_icon_url")]
        private string ProxyIconUrl { get; }

        public Footer(string text, string iconUrl, string proxyIconUrl)
        {
            Text = text;
            IconUrl = iconUrl;
            ProxyIconUrl = proxyIconUrl;
        }
    }

    #endregion

    #region Attachment Classes
    /// <summary>
    /// Enum for attachment content type
    /// </summary>
    private enum AttachmentContentType
    {
        Png,
        Jpg
    }

    private class Attachment
    {
        /// <summary>
        /// Attachment data
        /// </summary>
        public byte[] Data { get; }
            
        /// <summary>
        /// File name for the attachment.
        /// Used in the url field of an image
        /// </summary>
        public string Filename { get; }
            
        /// <summary>
        /// Content type for the attachment
        /// https://developer.mozilla.org/en-US/docs/Web/HTTP/Basics_of_HTTP/MIME_types
        /// </summary>
        public string ContentType { get; }

        public Attachment(byte[] data, string filename, AttachmentContentType contentType)
        {
            Data = data;
            Filename = filename;

            switch (contentType)
            {
                case AttachmentContentType.Jpg:
                    ContentType = "image/jpeg";
                    break;
                    
                case AttachmentContentType.Png:
                    ContentType = "image/png";
                    break;
            }
        }

        public Attachment(byte[] data, string filename, string contentType)
        {
            Data = data;
            Filename = filename;
            ContentType = contentType;
        }
    }
        
    #endregion

    #region Config Classes

    public class DiscordMessageConfig
    {
        public string Content { get; set; }
            
        [JsonProperty("Webhook Override (Overrides the default webhook for this message)")]
        public string WebhookOverride { get; set; }
            
        [JsonProperty("Send Mode (Always, Random)")]
        [JsonConverter(typeof(StringEnumConverter))]
        public SendMode SendMode { get; set; }
            
        public EmbedConfig Embed { get; set; }
    }
        
    public class EmbedConfig
    {
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; }
            
        [JsonProperty("Title")]
        public string Title { get; set; }
            
        [JsonProperty("Description")]
        public string Description { get; set; }
            
        [JsonProperty("Url")]
        public string Url { get; set; }
            
        [JsonProperty("Embed Color")]
        public string Color { get; set; }
            
        [JsonProperty("Image Url")]
        public string Image { get; set; }
            
        [JsonProperty("Thumbnail Url")]
        public string Thumbnail { get; set; }
            
        [JsonProperty("Fields")]
        public List<FieldConfig> Fields { get; set; }
            
        [JsonProperty("Footer")]
        public FooterConfig Footer { get; set; }
    }
        
    public class FieldConfig
    {
        [JsonProperty("Title")]
        public string Title { get; set; }
            
        [JsonProperty("Value")]
        public string Value { get; set; }
            
        [JsonProperty("Inline")]
        public bool Inline { get; set; }

        [JsonProperty("Enabled")]
        public bool Enabled { get; set; }
    }

    public class FooterConfig
    {
        [JsonProperty("Icon Url")]
        public string IconUrl { get; set; }
            
        [JsonProperty("Text")]
        public string Text { get; set; }
            
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; }
    }
    #endregion
        
    #region Config Methods
    private DiscordMessage ParseMessage(DiscordMessageConfig config)
    {
        DiscordMessage message = new();

        if (!string.IsNullOrEmpty(config.Content))
        {
            message.AddContent(ParseField(config.Content));
        }

        EmbedConfig embedConfig = config.Embed;
        if (embedConfig != null && embedConfig.Enabled)
        {
            Embed embed = new();
            string title = ParseField(config.Embed.Title);
            if (!string.IsNullOrEmpty(title))
            {
                embed.AddTitle(title);
            }

            string description = ParseField(embedConfig.Description);
            if (!string.IsNullOrEmpty(description))
            {
                embed.AddDescription(description);
            }

            string url = ParseField(embedConfig.Url);
            if (!string.IsNullOrEmpty(url))
            {
                embed.AddUrl(url);
            }

            string color = ParseField(embedConfig.Color);
            if (!string.IsNullOrEmpty(color))
            {
                embed.AddColor(color);
            }

            string img = ParseField(embedConfig.Image);
            if (!string.IsNullOrEmpty(img))
            {
                embed.AddImage(img);
            }

            string thumbnail = ParseField(embedConfig.Thumbnail);
            if (!string.IsNullOrEmpty(thumbnail))
            {
                embed.AddThumbnail(thumbnail);
            }

            foreach (FieldConfig field in embedConfig.Fields.Where(f => f.Enabled))
            {
                string value = ParseField(field.Value);
                if (string.IsNullOrEmpty(value))
                {
                    //PrintWarning($"Field: {field.Title} was skipped because the value was null or empty.");
                    continue;
                }

                embed.AddField(field.Title, value, field.Inline);
            }

            if (embedConfig.Footer != null && embedConfig.Footer.Enabled)
            {
                if (string.IsNullOrEmpty(embedConfig.Footer.Text) &&
                    string.IsNullOrEmpty(embedConfig.Footer.IconUrl))
                {
                    AddPluginInfoFooter(embed);
                }
                else
                {
                    string text = ParseField(embedConfig.Footer.Text);
                    string footerUrl = ParseField(embedConfig.Footer.IconUrl);
                    embed.AddFooter(text, footerUrl);
                }
            }

            message.AddEmbed(embed);
        }

        return message;
    }
    #endregion
    #endregion
}