using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Configuration;

namespace Oxide.Plugins
{
    [Info("Extended Recycler", "beee/The Friendly Chap", "1.2.6")]
    [Description("Extend recyclers for personal use with per player limits")]

/* Version History : 
    1.2.4 : Added logic to create a VIP starting balance system.
	1.2.5 : Added Logo and MIT Licence
	1.2.6 : Removed Logo.
*/
/*	MIT License

	©2024 The Friendly Chap

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in all
	copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
	SOFTWARE.
*/
    public class ExtendedRecycler : RustPlugin
    {
        #region Vars

        private const ulong skinID = 1594245394;
        private const string prefab = "assets/bundled/prefabs/static/recycler_static.prefab";
        private static ExtendedRecycler plugin;
        private const string permUse = "extendedrecycler.use";
        private const string permUnlimited = "extendedrecycler.unlimited";
        private const string permVIP = "extendedrecycler.vip";

        RecyclersData recData;
        private DynamicConfigFile data;

        ProtectionProperties recyclerProtection;
        ProtectionProperties originalRecyclerProtection;

        #endregion

        #region Config

        private static ConfigData config;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "1. Pickup settings:")]
            public OPickup pickup;

            [JsonProperty(PropertyName = "2. Craft settings:")]
            public OCraft craft;

            [JsonProperty(PropertyName = "3. Destroy settings:")]
            public ODestroy destroy;

            [JsonProperty(PropertyName = "4. Damage settings:")]
            public ODamage damage;

            public class OPickup
            {
                [JsonProperty(PropertyName = "1. Enabled for personal recyclers (placed by player)")]
                public bool personal;

                [JsonProperty(PropertyName = "2. Check ability to build for pickup")]
                public bool privilege;

                [JsonProperty(PropertyName = "3. Only owner can pickup")]
                public bool onlyOwner;
            }

            public class OCraft
            {
                [JsonProperty(PropertyName = "1. Enabled")]
                public bool enabled;

                [JsonProperty(PropertyName = "2. Cost (shortname - amount):")]
                public Dictionary<string, int> cost;

                
                [JsonProperty(PropertyName = "3. Default Balance per Player:")]
                public int defaultBalance = 2;

                [JsonProperty(PropertyName = "3. VIP Balance per Player:")]
                public int VIPBalance = 10;
            }

            public class ODestroy
            {
                [JsonProperty(PropertyName = "1. Check ground for recyclers (destroy on missing)")]
                public bool checkGround;

                [JsonProperty(PropertyName = "2. Give item on destroy recycler")]
                public bool destroyItem;

                [JsonProperty(PropertyName = "3. Effects on destroy recycler")]
                public List<string> effects;
            }

            public class ODamage
            {
                [JsonProperty(PropertyName = "1. Allow damage to recycler")]
                public bool allowDamage;

                [JsonProperty(PropertyName = "2. Reduce damage to recycler by %")]
                public float reduceDamagePercent;
            }
        }

        private ConfigData GetDefaultConfig()
        {
            return new ConfigData
            {
                pickup = new ConfigData.OPickup
                {
                    personal = false,
                    privilege = true,
                    onlyOwner = false
                },
                craft = new ConfigData.OCraft
                {
                    enabled = true,
                    cost = new Dictionary<string, int>
                    {
                        {"scrap", 500},
                        {"metal.fragments", 5000},
                        {"metal.refined", 50},
                        {"gears", 10}
                    },
                    defaultBalance = 2,
                    VIPBalance = 10
                },
                destroy = new ConfigData.ODestroy
                {
                    checkGround = true,
                    destroyItem = true,
                    effects = new List<string>
                    {
                        "assets/bundled/prefabs/fx/item_break.prefab",
                        "assets/bundled/prefabs/fx/impacts/stab/rock/stab_rock_01.prefab"
                    }
                },
                damage = new ConfigData.ODamage
                {
                    allowDamage = true,
                    reduceDamagePercent = 50f
                }
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                config = Config.ReadObject<ConfigData>();
            }
            catch
            {
                LoadDefaultConfig();
            }

            if(config.damage == null)
            {
                config.damage = new ConfigData.ODamage()
                {
                    allowDamage = true,
                    reduceDamagePercent = 50f
                };
            }

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            PrintError("Configuration file is corrupt(or not exists), creating new one!");
            config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        #endregion

        #region Language

        private Dictionary<string, string> EN = new Dictionary<string, string>
        {
            {"Name", "Recycler"},
            {"Pickup", "You picked up recycler!"},
            {"Receive", "You received recycler!"},
            {"Disabled", "Pickup disabled!"},
            {"Build", "You must have ability to build to do that!"},
            {"Damaged", "Recycler was recently damaged, you can pick it up in next 30s!"},
            {"NoCraft", "Craft disabled!"},
            {"Owner", "Only owner can pickup recycler!"},
            {"Craft", "For craft you need more resources:\n{0}"},
            {"Permission", "You need permission to do that!"},
            {"Claimed", "You claimed a recycler! your current balance is {0}."},
            {"LimitExceeded", "You reached your crafting limit!"},
            {"Balance", "Your current balance is {0}."}
        };

        private void message(BasePlayer player, string key, params object[] args)
        {
            if (player == null)
            {
                return;
            }
            
            var message = string.Format(lang.GetMessage(key, this, player.UserIDString), args);
            player.ChatMessage(message);
        }

        #endregion

        #region Oxide Hooks

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            CheckDeploy(go.ToBaseEntity());
        }

        private void OnServerInitialized()
        {
            plugin = this;
            lang.RegisterMessages(EN, this);
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permUnlimited, this);
            permission.RegisterPermission(permVIP, this);
            CheckRecyclers();            
            LoadData();
			// ShowLogo();
        }

        private void OnHammerHit(BasePlayer player, HitInfo info)
        {
            CheckHit(player, info?.HitEntity);
        }

        private void Unload()
        {
            if(config.damage.allowDamage)
            {
                // In case unloaded before OnServerInitialized was called
                if (originalRecyclerProtection == null)
                    return;

                foreach (var recycler in UnityEngine.Object.FindObjectsOfType<Recycler>())
                {
                    recycler.baseProtection = originalRecyclerProtection;
                }

                UnityEngine.Object.Destroy(recyclerProtection);
            }
            SaveData();
        }

        private void OnServerSave() => SaveData();

        #endregion

        #region Core

        private BaseEntity SpawnRecycler(Vector3 position, Quaternion rotation = default(Quaternion), ulong ownerID = 0)
        {
            var recycler = GameManager.server.CreateEntity(prefab, position, rotation);
            if (recycler == null)
            {
                return null;
            }

            recycler.skinID = skinID;
            recycler.OwnerID = ownerID;
            recycler.gameObject.AddComponent<ExtendedRecyclerComponent>();
            recycler.Spawn();

            return recycler;
        }

        private void CheckRecyclers()
        {
            if(config.damage.allowDamage)
            {
                recyclerProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
                recyclerProtection.name = "ExtendedRecyclerProtection";
                recyclerProtection.Add(config.damage.reduceDamagePercent/100f); // Reduce damage from all types
            }

            foreach (var recycler in UnityEngine.Object.FindObjectsOfType<Recycler>())
            {
                if (IsRecycler(recycler.skinID) && recycler.GetComponent<ExtendedRecyclerComponent>() == null)
                {
                    if (config.damage.allowDamage && originalRecyclerProtection == null)
                        originalRecyclerProtection = recycler.baseProtection;

                    recycler.gameObject.AddComponent<ExtendedRecyclerComponent>();
                }
            }
        }

        private void GiveRecycler(BasePlayer player, bool pickup = false, bool free = false)
        {
            var item = CreateItem();
            if (item != null && player != null)
            {
                player.GiveItem(item);
                
                if(pickup)
                {
                    message(player, "Pickup");
                }
                else
                {
                    if(permission.UserHasPermission(player.UserIDString, permUnlimited) || free)
                    {
                        message(player, "Receive");
                    }
                    else
                    {
                        int remainingBalance = SpendFromBalance(player);
                        message(player, "Claimed", remainingBalance);
                    }
                }
            }
        }

        private void GiveRecycler(Vector3 position)
        {
            var item = CreateItem();
            item?.Drop(position, Vector3.down);
        }

        private Item CreateItem()
        {
            var item = ItemManager.CreateByName("box.repair.bench", 1, skinID);
            if (item != null)
            {
                item.name = plugin?.GetRecyclerName();
            }
           
            return item;
        }

        private void CheckDeploy(BaseEntity entity)
        {
            if (entity == null)
            {
                return;
            }

            if (!IsRecycler(entity.skinID))
            {
                return;
            }

            var transform = entity.transform;
            BaseEntity recycler = SpawnRecycler(transform.position, transform.rotation, entity.OwnerID);
            entity.transform.position -= new Vector3(0, 3, 0);
            entity.SendNetworkUpdate();
            NextFrame(() =>
            {
                if (entity.IsValid() == true && entity.IsDestroyed == false)
                {
                    if(recycler.IsValid() && entity.HasParent() && entity.GetParentEntity() is Tugboat)
                    {
                        recycler.SetParent(entity.GetParentEntity(), true, true);
                    }
                    
                    entity.Kill();
                }
            });
        }

        private void CheckHit(BasePlayer player, BaseEntity entity)
        {
            if (entity == null)
            {
                return;
            }

            if (!IsRecycler(entity.skinID))
            {
                return;
            }

            NextFrame(() =>
            {
                if (entity.IsValid() == true)
                {
                    entity.GetComponent<ExtendedRecyclerComponent>()?.TryPickup(player);
                }
            });
        }

        [ChatCommand("recycler.craft")]
        private void Craft(BasePlayer player)
        {
            if (CanCraft(player))
            {
                GiveRecycler(player);
            }
        }
        
        [ChatCommand("recycler.balance")]
        private void Balance(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, permUse))
            {
                message(player, "Permission");
                return;
            }

            string balance = GetBalance(player).ToString();

            if (permission.UserHasPermission(player.UserIDString, permUnlimited))
            {
                balance = "Unlimited";
            }

            message(player, "Balance", balance);
        }

        private bool CanCraft(BasePlayer player)
        {
            if (!config.craft.enabled)
            {
                message(player, "NoCraft");
                return false;
            }

            if (!permission.UserHasPermission(player.UserIDString, permUse))
            {
                message(player, "Permission");
                return false;
            }

            CheckEntry(player);

            if(!permission.UserHasPermission(player.UserIDString, permUnlimited) 
            && recData.Players[player.userID].Balance <= 0)
            {
                message(player, "LimitExceeded");
                return false;
            }

            var recipe = config.craft.cost;
            var more = new Dictionary<string, int>();

            foreach (var component in recipe)
            {
                var name = component.Key;
                var has = player.inventory.GetAmount(ItemManager.FindItemDefinition(component.Key).itemid);
                var need = component.Value;
                if (has < component.Value)
                {
                    if (!more.ContainsKey(name))
                    {
                        more.Add(name, 0);
                    }

                    more[name] += need;
                }
            }

            if (more.Count == 0)
            {
                foreach (var item in recipe)
                {
                    player.inventory.Take(null, ItemManager.FindItemDefinition(item.Key).itemid, item.Value);
                }

                return true;
            }
            else
            {
                var text = "";

                foreach (var item in more)
                {
                    text += $" * {item.Key} x{item.Value}\n";
                }

                player.ChatMessage(string.Format(lang.GetMessage("Craft", this), text));
                return false;
            }
        }

        #endregion

        #region Helpers

        private string GetRecyclerName()
        {
            return lang.GetMessage("Name", this);
        }

        private bool IsRecycler(ulong skin)
        {
            return skin != 0 && skin == skinID;
        }

        private void CheckEntry(BasePlayer player)
        {
            if (!recData.Players.ContainsKey(player.userID))
            {
                recData.Players.Add(player.userID, new PlayerData
                {
                    DisplayName = player.displayName,
                    Balance = config.craft.defaultBalance
                });
                if (permission.UserHasPermission(player.UserIDString, permVIP)) 
                {
                    recData.Players[player.userID].Balance = config.craft.VIPBalance;
                }
            }
        }

        private void CheckEntry(ulong playerId)
        {
            if (!recData.Players.ContainsKey(playerId))
            {
                recData.Players.Add(playerId, new PlayerData
                {
                    DisplayName = covalence.Players.FindPlayerById(playerId.ToString())?.Name ?? "NoName",
                    Balance = config.craft.defaultBalance
                });
                if (permission.UserHasPermission(playerId.ToString(), permVIP))
                {
                    recData.Players[playerId].Balance = config.craft.VIPBalance;
                }
            }
        }

        public int SpendFromBalance(BasePlayer player){
            CheckEntry(player);
            if(permission.UserHasPermission(player.UserIDString, permUnlimited)){ return 999; }

            recData.Players[player.userID].Balance -= 1;
            if(recData.Players[player.userID].Balance < 0){
                recData.Players[player.userID].Balance = 0;
            }

            return recData.Players[player.userID].Balance;
        }

        private void SetBalance(BasePlayer player, int newBalance)
        {
            CheckEntry(player);
            
            recData.Players[player.userID].Balance = newBalance;
        }

        private int AddBalance(BasePlayer player, int deposit)
        {
            CheckEntry(player);
            
            recData.Players[player.userID].Balance += deposit;

            return recData.Players[player.userID].Balance;
        }

        private int GetBalance(BasePlayer player)
        {
            CheckEntry(player);
            
            return recData.Players[player.userID].Balance;
        }
		
		private void ShowLogo()
        {
			Puts(" _______ __               _______        __                 __ __             ______ __           ©2024");
			Puts("|_     _|  |--.-----.    |    ___|.----.|__|.-----.-----.--|  |  |.--.--.    |      |  |--.---.-.-----.");
			Puts("  |   | |     |  -__|    |    ___||   _||  ||  -__|     |  _  |  ||  |  |    |   ---|     |  _  |  _  |");
			Puts("  |___| |__|__|_____|    |___|    |__|  |__||_____|__|__|_____|__||___  |    |______|__|__|___._|   __|");
			Puts("                         Extended Recycler v1.2.5                 |_____| thefriendlychap.co.za |__|");      
        }
        #endregion

        #region Command

        [ConsoleCommand("recycler.give")]
        private void Cmd(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin && arg.Args?.Length > 0)
            {
                var player = BasePlayer.Find(arg.Args[0]) ?? BasePlayer.FindSleeping(arg.Args[0]);
                if (player == null)
                {
                    PrintWarning($"We can't find player with that name/ID! {arg.Args[0]}");
                    return;
                }

                GiveRecycler(player, false, true);
            }
        }

        [ConsoleCommand("recycler.setbalance")]
        private void SetBalanceCMD(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin && arg.Args?.Length > 1)
            {
                var player = BasePlayer.Find(arg.Args[0]) ?? BasePlayer.FindSleeping(arg.Args[0]);
                if (player == null)
                {
                    PrintWarning($"We can't find player with that name/ID! {arg.Args[0]}");
                    return;
                }

                int newBalance = 0;

                if (!int.TryParse(arg.Args[1], out newBalance))
                {
                    PrintWarning($"{arg.Args[1]} is not a valid number!");
                    return;
                }

                SetBalance(player, newBalance);
                Puts($"{arg.Args[0]} current balance is " + newBalance + ".");
                //player.ChatMessage($"Your current recycler craft balance is " + newBalance + ".");
            }
        }

        [ConsoleCommand("rec_wipe")]
        private void ccmdPCWipe(ConsoleSystem.Arg arg)
        {if (arg.IsAdmin)
            {
                recData = new RecyclersData();
                Puts("Extended Recycler wiped successfully.");
                SaveData();
            }
        }

        #endregion

        #region Data
        private void SaveData()
        {
            if(recData == null) return;
            
            data.WriteObject(recData);
        }

        private void LoadData()
        {
            data = Interface.Oxide.DataFileSystem.GetFile("ExtendedRecycler");
            try
            {
                recData = data.ReadObject<RecyclersData>();
                if(recData == null) recData = new RecyclersData();
            }
            catch
            {
                recData = new RecyclersData();
            }
        }

        private class RecyclersData
        {
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();
        }

        private class PlayerData
        {
            public string DisplayName = string.Empty;
            public int Balance = config.craft.defaultBalance;
        }

        #endregion

        #region Scripts

        private class ExtendedRecyclerComponent : MonoBehaviour
        {
            private Recycler recycler;

            private void Awake()
            {
                recycler = GetComponent<Recycler>();
                
                if(config.damage.allowDamage)
                {
                    recycler.baseProtection = plugin.recyclerProtection;
                }

                if (config.destroy.checkGround)
                {
                    InvokeRepeating("CheckGround", 5f, 5f);
                }
            }

            private void CheckGround()
            {
                if(recycler.HasParent() && recycler.GetParentEntity() is Tugboat) 
                {
                    CancelInvoke("CheckGround");
                    return;
                }
                
                RaycastHit rhit;
                var cast = Physics.Raycast(recycler.transform.position + new Vector3(0, 0.1f, 0), Vector3.down,
                    out rhit, 4f, LayerMask.GetMask("Terrain", "Construction"));
                var distance = cast ? rhit.distance : 3f;

                if (distance > 0.2f)
                {
                    GroundMissing();
                }
            }

            private void GroundMissing()
            {
                recycler.Kill();

                if (config.destroy.destroyItem)
                {
                    plugin.GiveRecycler(recycler.transform.position);
                }

                foreach (var effect in config.destroy.effects)
                {
                    Effect.server.Run(effect, recycler.transform.position);
                }
            }

            public void TryPickup(BasePlayer player)
            {
                if (config.pickup.personal == false)
                {
                    plugin.message(player, "Disabled");
                    return;
                }

                if (config.pickup.privilege && !player.CanBuild())
                {
                    plugin.message(player, "Build");
                    return;
                }

                if (config.pickup.onlyOwner && recycler.OwnerID != player.userID)
                {
                    plugin.message(player, "Owner");
                    return;
                }

                if (recycler.SecondsSinceDealtDamage < 30f)
                {
                    plugin.message(player, "Damaged");
                    return;
                }
                
                recycler.DropItems();
                recycler.Kill();
                plugin.GiveRecycler(player, true);
            }

            public void DoDestroy()
            {
                Destroy(this);
            }
        }

        #endregion
    }
}