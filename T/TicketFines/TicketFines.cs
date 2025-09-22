using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Ticket Fines", "McJackson164", "1.1.4")]
    [Description("A police-like system to issue tickets and impose fines.")]
    internal sealed class TicketFines : CovalencePlugin
    {
        #region Fields

        private const string PermissionTicketsIssue = "ticketfines.issue";
        private const string PermissionTicketsDemand = "ticketfines.demand";
        private const string PermissionTicketsClose = "ticketfines.close";
        private const string PermissionTicketsEdit = "ticketfines.edit";

        [PluginReference]
        private Plugin Economics;

        [PluginReference]
        private Plugin ServerRewards;

        private StoredData storedData;
        private Configuration config;

        private Dictionary<Ticket, string> demandedTickets = new Dictionary<Ticket, string>();

        private string currency = "";

        #endregion Fields

        #region Init

        private void Init()
        {
            permission.RegisterPermission(PermissionTicketsIssue, this);
            permission.RegisterPermission(PermissionTicketsDemand, this);
            permission.RegisterPermission(PermissionTicketsClose, this);
            permission.RegisterPermission(PermissionTicketsEdit, this);

            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);

            var lastTicket = storedData.Tickets.Values.Select(l => l.Last()).OrderByDescending(t => t.TicketID).FirstOrDefault();
            Ticket.CurrentID = lastTicket != null ? lastTicket.TicketID : 0;
        }

        #endregion Init

        #region Hooks

        private void Loaded()
        {
            if (config.EnableFines)
            {
                if (new[] { config.UseEconomics, config.UseServerRewards, config.UseCustomCurrency }.Count(boolean => boolean) > 1)
                {
                    throw new ArgumentException("You can only use either Economics, Server Rewards or a custom currency to pay fines! Please adjust the configuration!");
                }
                else if (config.UseCustomCurrency)
                {
                    if (string.IsNullOrEmpty(config.CustomCurrencyItem))
                    {
                        throw new ArgumentException("You have to set a custom currency item in the configuration!");
                    }

                    var itemDefinition = ItemManager.FindItemDefinition(config.CustomCurrencyItem);
                    if (!itemDefinition)
                    {
                        throw new ArgumentException("The custom currency item does not exist! Please change the custom currency item in the configuration!");
                    }

                    currency = itemDefinition.displayName.translated;
                }
                else if (config.UseServerRewards)
                {
#if !RUST
                    throw new NotSupportedException("You can not use a Rust plugin in another game. Please set 'Use Server Rewards' to false in the configuration of this plugin.");
#endif
                    currency = "Point(s)";
                }
                else if (config.UseEconomics) currency = config.FineCurrency;
            }

            if (Economics == null)
            {
                Puts("Economics not found!");
            }
        }

        private void OnServerSave() => SaveData();

        private void Unload() => SaveData();

        #endregion Hooks

        #region Commands

        [Command("fine")]
        private void CommandTicket(IPlayer iplayer, string command, string[] args)
        {
            var hasPermissionIssue = iplayer.HasPermission(PermissionTicketsIssue);
            var hasPermissionDemand = iplayer.HasPermission(PermissionTicketsDemand);
            var hasPermissionClose = iplayer.HasPermission(PermissionTicketsClose);
            var hasPermissionEdit = iplayer.HasPermission(PermissionTicketsEdit);

            if (args.Length < 1)
            {
                var message = "Usage: /fine list|pay";
                if (hasPermissionIssue) message += "|issue";
                if (hasPermissionDemand) message += "|demand";
                if (hasPermissionClose) message += "|close";
                if (hasPermissionEdit) message += "|edit";

                iplayer.Message(message);
                return;
            };

            switch (args[0].ToLower())
            {
                case "list":
                    IPlayer listTarget = (hasPermissionDemand || hasPermissionClose || hasPermissionEdit) && args.Length >= 2 ? players.FindPlayer(args[1]) : iplayer;
                    if (listTarget == null)
                    {
                        iplayer.Message(lang.GetMessage("Exception_PlayerNotFound", this, iplayer.Id));
                        return;
                    }

                    var tickets = GetUnpaidTickets(listTarget, 10);
                    if (tickets == null) return;

                    iplayer.Message(lang.GetMessage("CmdTicket_List_Header", this, iplayer.Id));
                    foreach (var unpaidTicket in tickets)
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_List_Entry", iplayer, unpaidTicket.TicketID, $"{unpaidTicket.IssueDate.Month}/{unpaidTicket.IssueDate.Day}", unpaidTicket.Fine, currency, unpaidTicket.Note));
                    }
                    iplayer.Message(lang.GetMessage("CmdTicket_List_UsagePay", this, iplayer.Id));

                    return;

                case "pay":
                    if (args.Length < 2)
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Pay_Usage", this, iplayer.Id));
                        return;
                    }

                    var ticket = GetTicketByIDString(args[1]);
                    if (ticket == null) return;

                    var ticketPaid = PayTicket(iplayer, ticket);

                    if (ticketPaid)
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_Pay_Success", iplayer, ticket.TicketID));
                    }
                    else
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_Pay_Failed", iplayer, ticket.TicketID));
                    }

                    if (IsTicketDemanded(ticket))
                    {
                        var demanding = GetActivePlayerByUserID(demandedTickets[ticket]);
                        if (ticketPaid) demandedTickets.Remove(ticket);
                        if (demanding == null) return;
                        demanding.Message(GetMessageFormatted("CmdTicket_Pay_DemandResult", iplayer, ticket.TicketID, (ticketPaid ? "" : "not ")));
                    }
                    return;

                case "issue":
                    if (!hasPermissionIssue) return;
                    if (args.Length < 3)
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Issue_Usage", this, iplayer.Id));
                        return;
                    }

                    double fine;
                    if (!double.TryParse(args[2], out fine) || fine < 0d)
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Issue_InvalidFine", this, iplayer.Id));
                        return;
                    }

                    var target = players.FindPlayer(args[1]);
                    if (target == null)
                    {
                        iplayer.Message(lang.GetMessage("Exception_PlayerNotFound", this, iplayer.Id));
                        return;
                    }

                    var issuedTicket = IssueTicket(iplayer, target, fine, args.Length > 3 ? args[3] : null);
                    if (issuedTicket == null)
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_Issue_Failed", iplayer, target.Name));
                        return;
                    }
                    iplayer.Message(GetMessageFormatted("CmdTicket_Issue_Success", iplayer, issuedTicket.TicketID, target.Name));
                    return;

                case "demand":
                    if (!hasPermissionDemand) return;
                    if (args.Length < 2)
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Demand_Usage", this, iplayer.Id));
                        return;
                    }

                    var demandedTicket = GetTicketByIDString(args[1]);
                    if (demandedTicket == null)
                    {
                        iplayer.Message(GetMessageFormatted("Exception_TicketNotFound", iplayer, args[1]));
                        return;
                    }

                    var demandedPlayer = GetActivePlayerByUserID(demandedTicket.ReceiverID);
                    if (demandedPlayer == null)
                    {
                        iplayer.Message(lang.GetMessage("Exception_PlayerNotFound", this, iplayer.Id));
                        return;
                    }

                    if (demandedTickets.ContainsKey(demandedTicket))
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Demand_TicketAlreadyDemanded", this, iplayer.Id));
                        return;
                    }

                    if (config.MaxDemandDistance > 0)
                    {
                        if (GenericDistance(iplayer.Position(), demandedPlayer.Position()) > config.MaxDemandDistance)
                        {
                            iplayer.Message(lang.GetMessage("CmdTicket_Demand_TargetOutOfRange", this, iplayer.Id));
                            return;
                        }
                    }

                    if (!DemandTicket(iplayer, demandedTicket))
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_Demand_Failure", iplayer, demandedTicket.TicketID, demandedPlayer.Name));
                        return;
                    }

                    demandedPlayer.Message(GetMessageFormatted("CmdTicket_Demand_TargetNotification", iplayer, iplayer.Name, demandedTicket.TicketID, demandedTicket.Fine, currency));
                    demandedPlayer.Message(GetMessageFormatted("CmdTicket_Demand_TargetUsage", iplayer, demandedTicket.TicketID));
                    iplayer.Message(GetMessageFormatted("CmdTicket_Demand_Success", iplayer, demandedTicket.TicketID, demandedPlayer.Name));

                    return;

                case "close":
                    if (!hasPermissionClose) return;
                    if (args.Length < 2)
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Close_Usage", this, iplayer.Id));
                        return;
                    }

                    var ticketToClose = GetTicketByIDString(args[1]);

                    if (ticketToClose == null)
                    {
                        iplayer.Message(GetMessageFormatted("Exception_TicketNotFound", iplayer, args[1]));
                        return;
                    }

                    if (!CloseTicket(ticketToClose, $"{iplayer.Name}:{iplayer.Id}"))
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_Close_Failure", iplayer, ticketToClose.TicketID));
                        return;
                    }

                    iplayer.Message(GetMessageFormatted("CmdTicket_Close_Success", iplayer, ticketToClose.TicketID));
                    return;

                case "edit":
                    if (!hasPermissionEdit) return;
                    if (args.Length < 4)
                    {
                        iplayer.Message(lang.GetMessage("CmdTicket_Edit_Usage", this, iplayer.Id));
                        iplayer.Message(lang.GetMessage("CmdTicket_Edit_Fields", this, iplayer.Id));
                        return;
                    }

                    var ticketToEdit = GetTicketByIDString(args[1]);

                    if (ticketToEdit == null)
                    {
                        iplayer.Message(GetMessageFormatted("Exception_TicketNotFound", iplayer, args[1]));
                        return;
                    }

                    if (!EditTicket(ticketToEdit, (TicketEditField)Enum.Parse(typeof(TicketEditField), args[2]), args[3]))
                    {
                        iplayer.Message(GetMessageFormatted("CmdTicket_Edit_Failure", iplayer, ticketToEdit.TicketID));
                    }

                    iplayer.Message(GetMessageFormatted("CmdTicket_Edit_Success", iplayer, ticketToEdit.TicketID));
                    return;

                default:
                    return;
            }
        }

        #endregion Commands

        #region Methods

        private Ticket IssueTicket(string issuer, IPlayer target, double fine, string note = null)
        {
            if (config.EnableFines)
            {
                if (fine < 0d) return null;
                if (fine > config.MaxFine) fine = config.MaxFine;
            }
            else
            {
                fine = 0d;
            }

            if (config.EnableNotes)
            {
                var noteIsNull = string.IsNullOrEmpty(note);
                if (config.RequireNote && noteIsNull) return null;
                if (!noteIsNull && config.MaxNoteLength > 0 && note.Length > config.MaxNoteLength) note = note.Substring(0, config.MaxNoteLength);
            }
            else
            {
                note = null;
            }

            var ticket = new Ticket(issuer, target.Id, fine, note);

            if (storedData.Tickets.ContainsKey(target.Id))
            {
                storedData.Tickets[target.Id].Add(ticket);
            }
            else
            {
                storedData.Tickets.Add(target.Id, new List<Ticket>() { ticket });
            }

            var issuerPlayer = players.FindPlayer(ticket.IssuerID);
            var issuerName = issuerPlayer != null ? issuerPlayer.Name : ticket.IssuerID;

#if RUST
            var rustPlayer = target.Object as BasePlayer;
            if (config.EnableNotes && rustPlayer && rustPlayer.IsAlive())
            {
                var item = ItemManager.CreateByName("note");
                item.name = "Ticket";
                item.skin = 2640270407;
                item.text = $"TICKET\n----------------------------------\nID:\t\t\t\t{ticket.TicketID}\nIssued at:\t{ticket.IssueDate}\nIssued by:\t{issuerName}\nFine:\t\t\t{ticket.Fine} {currency}" + (string.IsNullOrEmpty(note) ? "" : $"\nNote:\t\t\t{ticket.Note}");
                rustPlayer?.inventory.GiveItem(item);
            }
#endif
            if (target.IsConnected)
            {
                if (!string.IsNullOrEmpty(note))
                {
                    target.Message(GetMessageFormatted("Method_IssueTicket_TargetNotificationWithNote", target, ticket.Fine, currency, issuerName, ticket.Note));
                }
                else
                {
                    target.Message(GetMessageFormatted("Method_IssueTicket_TargetNotification", target, ticket.Fine, currency, issuerName));
                }
            }

            Interface.CallHook("OnTicketIssued", ticket.TicketID, ticket.IssuerID, ticket.ReceiverID, ticket.Fine, ticket.Note);

            if (config.EnableFines && config.AutoWithdraw)
            {
                if (PayTicket(target, ticket))
                {
                    target.Message(lang.GetMessage("Method_IssueTicket_AutoWithdraw_Success", this, target.Id));
                }
                else
                {
                    target.Message(lang.GetMessage("Method_IssueTicket_AutoWithdraw_Failure", this, target.Id));
                }
            }

            return ticket;
        }

        private Ticket IssueTicket(IPlayer player, IPlayer target, double fine, string note = null) => IssueTicket(player.Id, target, fine, note);

        private bool DemandTicket(IPlayer player, Ticket ticket)
        {
            demandedTickets.Add(ticket, player.Id);
            Interface.CallHook("OnTicketDemanded", player.Id, ticket.ReceiverID, ticket.TicketID);
            return true;
        }

        private bool PayTicket(IPlayer player, Ticket ticket)
        {
            if (ticket == null) return false;
            if (ticket.IsClosed) return false;
            if (ticket.ReceiverID != player.Id) return false;

            var target = demandedTickets.ContainsKey(ticket) ? demandedTickets[ticket] : ticket.IssuerID;

            if (config.EnableFines && ticket.Fine > 0)
            {
                if (config.UseEconomics && Economics != null)
                {
                    if (config.DemandantReceiveFine)
                    {
                        if (!Economics.Call<bool>("Transfer", player.Id, target, ticket.Fine)) return false;
                    }
                    else
                    {
                        if (!Economics.Call<bool>("Withdraw", player.Id, ticket.Fine)) return false;
                    }
                }
                else if (config.UseServerRewards && ServerRewards != null)
                {
                    var serverRewardsResponse = ServerRewards.Call<object>("TakePoints", player.Id, Convert.ToInt32(ticket.Fine));
                    if (serverRewardsResponse == null) return false;
                    if (config.DemandantReceiveFine)
                    {
                        ServerRewards.Call<object>("AddPoints", target, Convert.ToInt32(ticket.Fine));
                    }
                }
                else
                {
#if RUST
                    var rustPlayer = player.Object as BasePlayer;
                    var itemDefinition = ItemManager.FindItemDefinition(config.CustomCurrencyItem);
                    if (!itemDefinition) return false;
                    if (rustPlayer.inventory.GetAmount(itemDefinition.itemid) < ticket.Fine) return false;
                    rustPlayer.inventory.Take(null, itemDefinition.itemid, Convert.ToInt32(ticket.Fine));
                    if (config.DemandantReceiveFine)
                    {
                        var rustTargetPlayer = BasePlayer.Find(target);
                        if (rustTargetPlayer)
                        {
                            rustTargetPlayer.inventory.GiveItem(ItemManager.Create(itemDefinition, Convert.ToInt32(ticket.Fine)));
                        }
                    }
#elif HURTWORLD
                    // TODO: implement
                    return false;
#else
                    return false;
#endif
                }
            }

            CloseTicket(ticket, "PAID");
            Interface.CallHook("OnTicketPaid", player.Id, ticket.TicketID, ticket.Fine);
            return true;
        }

        private bool CloseTicket(Ticket ticket, string closedBy = null)
        {
            if (ticket.IsClosed) return false;
            ticket.IsClosed = true;
            ticket.ClosedBy = closedBy;
            ticket.CloseDate = DateTime.Now;
            Interface.CallHook("OnTicketClosed", ticket.TicketID, ticket.ReceiverID, ticket.ClosedBy);
            return true;
        }

        private bool EditTicket(Ticket ticket, TicketEditField field, object value)
        {
            switch (field)
            {
                case TicketEditField.FINE:
                    double newFine;
                    if (!double.TryParse(value.ToString(), out newFine)) return false;
                    ticket.Fine = newFine;
                    return true;

                case TicketEditField.NOTE:
                    if (!(value is string)) return false;
                    ticket.Note = value.ToString();
                    return true;
            }
            return false;
        }

        private bool DeleteTicket(Ticket ticket)
        {
            return storedData.Tickets[ticket.ReceiverID].Remove(ticket);
        }

        private Ticket GetTicketByID(uint ticketID)
        {
            var query = from outer in storedData.Tickets
                        from inner in outer.Value
                        where inner.TicketID == ticketID
                        select inner;

            if (!query.Any()) return null;
            return query.First();
        }

        private Ticket GetTicketByIDString(string ticketIDString)
        {
            uint ticketID;
            if (!uint.TryParse(ticketIDString, out ticketID)) return null;
            return GetTicketByID(ticketID);
        }

        private List<Ticket> GetUnpaidTickets(IPlayer player, int limit = 100)
        {
            if (!HasTickets(player)) return null;
            return storedData.Tickets[player.Id].Where(ticket => !ticket.IsClosed).Take(limit).ToList();
        }

        private bool HasTickets(IPlayer player) => storedData.Tickets.ContainsKey(player.Id) && storedData.Tickets[player.Id].Count > 0;

        private bool IsTicketDemanded(Ticket ticket) => demandedTickets.ContainsKey(ticket);

        #endregion Methods

        #region API

        private List<Dictionary<string, object>> API_GetAllTicketsOfPlayer(IPlayer player)
        {
            if (!HasTickets(player)) return null;
            return TicketListToDictionary(storedData.Tickets[player.Id]);
        }

        private List<Dictionary<string, object>> API_GetUnpaidTicketsOfPlayer(IPlayer player)
        {
            if (!HasTickets(player)) return null;
            return TicketListToDictionary(GetUnpaidTickets(player));
        }

        private List<Dictionary<string, object>> API_GetPaidTicketsOfPlayer(IPlayer player)
        {
            if (!HasTickets(player)) return null;
            return TicketListToDictionary(storedData.Tickets[player.Id].Where(ticket => ticket.IsClosed));
        }

        private uint API_IssueTicket(string issuer, IPlayer target, double amount) => API_IssueTicket(issuer, target, amount, null);

        private uint API_IssueTicket(string issuer, IPlayer target, double amount, string note) => IssueTicket(issuer, target, amount, note)?.TicketID ?? 0;

        private bool API_PayTicketByID(IPlayer player, string ticketID, bool withdraw = true)
        {
            var ticket = GetTicketByIDString(ticketID);
            if (ticket == null) return false;

            return PayTicket(player, ticket);
        }

        private bool API_PayOldestTicketOfPlayer(IPlayer player)
        {
            if (!HasTickets(player)) return false;
            var ticket = storedData.Tickets[player.Id].First();
            return PayTicket(player, ticket);
        }

        private bool API_CloseTicketByID(string ticketID, string closedBy = null)
        {
            var ticket = GetTicketByIDString(ticketID);
            if (ticket == null) return false;
            return CloseTicket(ticket, closedBy);
        }

        private bool API_DeleteTicketByID(string ticketID)
        {
            var ticket = GetTicketByIDString(ticketID);
            if (ticket == null) return false;
            return DeleteTicket(ticket);
        }

        #endregion API

        #region Helper

        private List<Dictionary<string, object>> TicketListToDictionary(IEnumerable<Ticket> ticketList)
        {
            List<Dictionary<string, object>> ticketDictList = new List<Dictionary<string, object>>();
            foreach (var ticket in ticketList)
            {
                ticketDictList.Add(ticket.ToDictionary());
            }
            return ticketDictList;
        }

        private IPlayer GetActivePlayerByUserID(string userID)
        {
            foreach (var player in players.Connected)
                if (player.Id == userID) return player;
            return null;
        }

        private float GenericDistance(GenericPosition a, GenericPosition b)
        {
            float x = a.X - b.X;
            float y = a.Y - b.Y;
            float z = a.Z - b.Z;
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        #endregion Helper

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Enable fines (requires Economics OR Server Rewards OR custom currency item)")]
            public bool EnableFines = true;

            [JsonProperty("Use 'Economics' plugin for payment (requires Economics)")]
            public bool UseEconomics = false;

            [JsonProperty("Use 'Server Rewards' plugin for payment (requires Server Rewards)")]
            public bool UseServerRewards = false;

            [JsonProperty("Use custom currency for payment")]
            public bool UseCustomCurrency = true;

            [JsonProperty("Custom Currency Item (requires 'Use custom currency' to be true. Use item shortname)")]
            public string CustomCurrencyItem = "scrap";

            [JsonProperty("Enable automatic withdraw of fines (requires 'Enable fines' to be true)")]
            public bool AutoWithdraw = false;

            [JsonProperty("The demandent will receive a paid fine. (if false, the fine amount will be deleted)")]
            public bool DemandantReceiveFine = true;

            [JsonProperty("Maximum fine per ticket (default 1000.0)")]
            public double MaxFine = 1000.0d;

            [JsonProperty("Currency (requires 'Use custom currency' to be true)")]
            public string FineCurrency = "Scrap";

            [JsonProperty("Enable notes (descriptive note attached to a ticket)")]
            public bool EnableNotes = true;

            [JsonProperty("Tickets require note (tickets won't be issued without a note)")]
            public bool RequireNote = false;

            [JsonProperty("Maximum note length (0 = unlimited)")]
            public int MaxNoteLength = 200;

            [JsonProperty("Maximum distance in which you are able to demand a ticket (0 = unlimited)")]
            public int MaxDemandDistance = 20;

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }

                if (!config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    Puts("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                Puts($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        #endregion Configuration

        #region Data

        private class StoredData
        {
            public Dictionary<string, List<Ticket>> Tickets = new Dictionary<string, List<Ticket>>();

            public StoredData()
            {
            }
        }

        private class Ticket
        {
            public static uint CurrentID = 0;

            public uint TicketID { get; }
            public string IssuerID { get; }
            public string ReceiverID { get; }
            public double Fine { get; set; }
            public string Note { get; set; }
            public bool IsClosed { get; set; }
            public string ClosedBy { get; set; }
            public DateTime IssueDate { get; set; }
            public DateTime CloseDate { get; set; }

            [JsonConstructor]
            public Ticket(uint ticketID, string issuerID, string receiverID, double fine, DateTime issueDate, string note = null, bool isClosed = false, string closedBy = null, DateTime? closeDate = null)
            {
                TicketID = ticketID;
                IssuerID = issuerID;
                ReceiverID = receiverID;
                Fine = fine;
                IssueDate = issueDate;
                Note = note;
                IsClosed = isClosed;
                ClosedBy = closedBy;
                if (closeDate != null) CloseDate = (DateTime) closeDate;
            }

            public Ticket(string issuerID, string receiverID, double fine, string note = null) : this(++CurrentID, issuerID, receiverID, fine, DateTime.Now, note)
            {
            }

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(this));
        }

        private enum TicketEditField
        {
            FINE,
            NOTE
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

        #endregion Data

        #region Localization

        private string GetMessageFormatted(string key, IPlayer player, params object[] args) => string.Format(lang.GetMessage(key, this, player.Id), args);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                // COMMANDS
                ["CmdTicket_List_Header"] = "ID\tDate\t\tFine\t\t\tNote\n--------------------------------------------------------------",
                ["CmdTicket_List_Entry"] = "{0}:\t{1}\t\t{2} {3}\t\t{4}",
                ["CmdTicket_List_UsagePay"] = "Use '/fine pay <ID>' to pay the fine of a ticket.",

                ["CmdTicket_Pay_Usage"] = "Usage: /fine pay <ticketID>",
                ["CmdTicket_Pay_Success"] = "Successfully paid ticket with id '{0}'!",
                ["CmdTicket_Pay_Failed"] = "Can not pay ticket with id '{0}'!",
                ["CmdTicket_Pay_DemandResult"] = "The demanded ticket with id '{0}' was {1}paid!",

                ["CmdTicket_Issue_Usage"] = "Usage: /fine issue <playerName> <fine> <note (optional)>",
                ["CmdTicket_Issue_InvalidFine"] = "Inavlid fine!",
                ["CmdTicket_Issue_Success"] = "Successfully issued ticket with id '{0}' to '{1}'!",
                ["CmdTicket_Issue_Failed"] = "Failed to issue ticket to '{0}'!",

                ["CmdTicket_Demand_Usage"] = "Usage: /fine demand <ticketID>",
                ["CmdTicket_Demand_TicketAlreadyDemanded"] = "Ticket already demanded!",
                ["CmdTicket_Demand_TargetOutOfRange"] = "Target is out of range!",
                ["CmdTicket_Demand_Success"] = "Successfully demanded the payment of the ticket with id '{0}' from '{1}'!",
                ["CmdTicket_Demand_Failure"] = "Failed to demand ticket with id '{0}' from '{1}'",
                ["CmdTicket_Demand_TargetNotification"] = "'{0}' demands you to pay ticket '{1}' with a fine of {2} {3}!",
                ["CmdTicket_Demand_TargetUsage"] = "Use '/fine pay {0}' to pay the demanded ticket.",

                ["CmdTicket_Close_Usage"] = "Usage: /fine close <ticketID>",
                ["CmdTicket_Close_Success"] = "Successfully closed ticket with id '{0}'!",
                ["CmdTicket_Close_Failure"] = "Failed to close ticket with id '{0}'!",

                ["CmdTicket_Edit_Usage"] = "Usage: /fine edit <ticketID> <field> <newValue>",
                ["CmdTicket_Edit_Fields"] = "Fields: FINE, NOTE",
                ["CmdTicket_Edit_Success"] = "Successfully edited ticket with id '{0}'!",
                ["CmdTicket_Edit_Failure"] = "Failed to edit ticket with id '{0}'!",

                // METHODS
                ["Method_IssueTicket_TargetNotificationWithNote"] = "You received a ticket with a fine of {0} {1}, issued by {2}! Note: {3}",
                ["Method_IssueTicket_TargetNotification"] = "You received a ticket with a fine of {0} {1}, issued by {2}!",
                ["Method_IssueTicket_AutoWithdraw_Success"] = "Automatic withdraw was successful!",
                ["Method_IssueTicket_AutoWithdraw_Failure"] = "Automatic withdraw was not successful! Please pay your ticket manually!",

                // GENERAL EXCEPTIONS
                ["Exception_PlayerNotFound"] = "Player not found!",
                ["Exception_TicketNotFound"] = "Ticket with id '{0}' not found!",
            }, this);
        }

        #endregion Localization
    }
}
