using ClientPrefsAPI;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using static CounterStrikeSharp.API.Core.Listeners;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace CS2_HideTeammates
{
	public class HideTeammates : BasePlugin
	{
		float TIMERTIME = 0.3f;
		static IClientPrefsAPI _CP_api;
		bool g_bEnable = true;
		int g_iMaxDistance = 8000;
		bool g_bHActWeapon = false;
		bool g_bHWeapon = false;

		bool[] g_bHide = new bool[65];
		int[] g_iDistance = new int[65];
		bool[] g_bRMB = new bool[65];

		//Client Crash Fix From: https://github.com/qstage/CS2-HidePlayers
		private static readonly MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState> StateTransition = new(GameData.GetSignature("StateTransition"));
		private readonly INetworkServerService networkServerService = new();
		private readonly CSPlayerState[] g_PlayerState = new CSPlayerState[65];

		List<CEntityInstance>[] g_Target = new List<CEntityInstance>[65];
		CDynamicProp[] g_Model = new CDynamicProp[65];
		CounterStrikeSharp.API.Modules.Timers.Timer g_Timer;

		public FakeConVar<bool> Cvar_Enable = new("css_ht_enabled", "Disabled/enabled [0/1]", true, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<bool>(false, true));
		public FakeConVar<int> Cvar_MaxDistance = new("css_ht_maximum", "The maximum distance a player can choose [1000-8000]", 8000, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<int>(1000, 8000));

		public FakeConVar<bool> Cvar_ActiveWeapon = new("css_ht_hide_activeweapon", "Disable/enable hiding active weapons [0/1]", false, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<bool>(false, true));
		public FakeConVar<bool> Cvar_Weapon = new("css_ht_hide_weapon", "Disable/enable hiding weapons [0/1]", false, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<bool>(false, true));
		public override string ModuleName => "Hide Teammates";
		public override string ModuleDescription => "A plugin that can !hide with individual distances";
		public override string ModuleAuthor => "DarkerZ [RUS]";
		public override string ModuleVersion => "1.DZ.4test2";
		public override void OnAllPluginsLoaded(bool hotReload)
		{
			try
			{
				PluginCapability<IClientPrefsAPI> CapabilityEW = new("clientprefs:api");
				_CP_api = IClientPrefsAPI.Capability.Get();
			}
			catch (Exception)
			{
				_CP_api = null;
				UI.PrintToConsole("ClientPrefs API Failed!", 15);
			}

			if (hotReload)
			{
				Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).ToList().ForEach(player =>
				{
					GetValue(player);
				});
			}
		}
		public override void Load(bool hotReload)
		{
			StateTransition.Hook(Hook_StateTransition, HookMode.Post);
			for (int i = 0; i < 65; i++) g_Target[i] = new List<CEntityInstance>();
			UI.Strlocalizer = Localizer;

			g_bEnable = Cvar_Enable.Value;
			Cvar_Enable.ValueChanged += (sender, value) =>
			{
				g_bEnable = value;
				UI.CvarChangeNotify(Cvar_Enable.Name, value.ToString(), Cvar_Enable.Flags.HasFlag(ConVarFlags.FCVAR_NOTIFY));
			};

			g_iMaxDistance = Cvar_MaxDistance.Value;
			Cvar_MaxDistance.ValueChanged += (sender, value) =>
			{
				if (value >= 1000 && value <= 8000) g_iMaxDistance = value;
				else g_iMaxDistance = 8000;
				UI.CvarChangeNotify(Cvar_MaxDistance.Name, value.ToString(), Cvar_MaxDistance.Flags.HasFlag(ConVarFlags.FCVAR_NOTIFY));
			};

			g_bHActWeapon = Cvar_ActiveWeapon.Value;
			Cvar_ActiveWeapon.ValueChanged += (sender, value) =>
			{
				g_bHActWeapon = value;
				UI.CvarChangeNotify(Cvar_ActiveWeapon.Name, value.ToString(), Cvar_ActiveWeapon.Flags.HasFlag(ConVarFlags.FCVAR_NOTIFY));
			};

			g_bHWeapon = Cvar_Weapon.Value;
			Cvar_Weapon.ValueChanged += (sender, value) =>
			{
				g_bHWeapon = value;
				UI.CvarChangeNotify(Cvar_Weapon.Name, value.ToString(), Cvar_Weapon.Flags.HasFlag(ConVarFlags.FCVAR_NOTIFY));
			};

			RegisterFakeConVars(typeof(ConVar));

			RegisterEventHandler<EventPlayerConnectFull>(OnEventPlayerConnectFull);
			RegisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
			RegisterEventHandler<EventPlayerDeath>(OnEventPlayerDeathPre);
			RegisterListener<OnMapStart>(OnMapStart_Listener);
			RegisterListener<OnMapEnd>(OnMapEnd_Listener);
			RegisterListener<CheckTransmit>(OnTransmit);
			RegisterListener<OnTick>(OnOnTick_Listener);

			CreateTimer();
		}

		public override void Unload(bool hotReload)
		{
			StateTransition.Unhook(Hook_StateTransition, HookMode.Post);
			DeregisterEventHandler<EventPlayerConnectFull>(OnEventPlayerConnectFull);
			DeregisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
			DeregisterEventHandler<EventPlayerDeath>(OnEventPlayerDeathPre);
			RemoveListener<OnMapStart>(OnMapStart_Listener);
			RemoveListener<OnMapEnd>(OnMapEnd_Listener);
			RemoveListener<CheckTransmit>(OnTransmit);
			RemoveListener<OnTick>(OnOnTick_Listener);

			CloseTimer();
		}

#nullable enable
		private void ForceFullUpdate(CCSPlayerController? player)
#nullable disable
		{
			if (player is null || !player.IsValid) return;

			var networkGameServer = networkServerService.GetIGameServer();
			networkGameServer.GetClientBySlot(player.Slot)?.ForceFullUpdate();

			player.PlayerPawn.Value?.Teleport(null, player.PlayerPawn.Value.EyeAngles, null);
		}

		private HookResult Hook_StateTransition(DynamicHook hook)
		{
			var player = hook.GetParam<CCSPlayerPawn>(0).OriginalController.Value;
			var state = hook.GetParam<CSPlayerState>(1);

			if (player is null) return HookResult.Continue;

			if (state != g_PlayerState[player.Index])
			{
				if (state == CSPlayerState.STATE_OBSERVER_MODE || g_PlayerState[player.Index] == CSPlayerState.STATE_OBSERVER_MODE)
				{
					ForceFullUpdate(player);
				}
			}

			g_PlayerState[player.Index] = state;

			return HookResult.Continue;
		}

		private void OnOnTick_Listener()
		{
			Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).ToList().ForEach(player =>
			{
				if ((player.Buttons & PlayerButtons.Attack2) != 0) g_bRMB[player.Slot] = true;
				else g_bRMB[player.Slot] = false;
			});
		}

		HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
		{
#nullable enable
			CCSPlayerController? player = @event.Userid;
#nullable disable
			if (player != null && player.IsValid)
			{
				g_bHide[player.Slot] = false;
				g_iDistance[player.Slot] = 0;
			}
			return HookResult.Continue;
		}
		HookResult OnEventPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
		{
			GetValue(@event.Userid);
			return HookResult.Continue;
		}

		[GameEventHandler(mode: HookMode.Pre)]
		private HookResult OnEventPlayerDeathPre(EventPlayerDeath @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;
			if (player != null && player.IsValid) RemoveModel(player);
			return HookResult.Continue;
		}

		void OnMapStart_Listener(string sMapName)
		{
			CreateTimer();
		}

		void OnMapEnd_Listener()
		{
			CloseTimer();
		}

		void OnTransmit(CCheckTransmitInfoList infoList)
		{
			if (!g_bEnable) return;
#nullable enable
			foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
#nullable disable
			{
				if (player == null || !player.IsValid || !player.Pawn.IsValid || player.Pawn.Value == null || player.Pawn.Value.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

				foreach (CEntityInstance targetTransmit in g_Target[player.Slot].ToList())
				{
					if (targetTransmit != null && targetTransmit.IsValid) info.TransmitEntities.Remove(targetTransmit);
				}
			}
		}

		void OnTimer()
		{
			if (!g_bEnable) return;
			Utilities.GetPlayers().Where(p => p.IsValid && p.Pawn.IsValid && p.Pawn.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE).ToList().ForEach(player =>
			{
				if (g_Model[player.Slot] == null || !g_Model[player.Slot].IsValid ||
					g_Model[player.Slot].CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.ModelName.CompareTo(player.Pawn.Value.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.ModelName) != 0)
						SetModel(player, player.Pawn.Value.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.ModelName);

				if(player.Pawn.Value.Render != Color.FromArgb(0, 255, 255, 255))
				{
					g_Model[player.Slot].Render = Color.FromArgb(player.Pawn.Value.Render.A, player.Pawn.Value.Render.R, player.Pawn.Value.Render.G, player.Pawn.Value.Render.B);
					Utilities.SetStateChanged(g_Model[player.Slot], "CBaseModelEntity", "m_clrRender");

					player.Pawn.Value.Render = Color.FromArgb(0, 255, 255, 255);
					Utilities.SetStateChanged(player.Pawn.Value, "CBaseModelEntity", "m_clrRender");
				}

				g_Target[player.Slot].Clear();
				if (g_bHide[player.Slot] && !g_bRMB[player.Slot])
				{
					Utilities.GetPlayers().Where(target => target != null && target.IsValid && target.Pawn.IsValid && target.Slot != player.Slot && target.Team == player.Team && target.Pawn.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE).ToList().ForEach(targetplayer =>
					{
						if (g_iDistance[player.Slot] == 0 || Distance(targetplayer.Pawn.Value?.AbsOrigin, player.Pawn.Value?.AbsOrigin) <= g_iDistance[player.Slot])
						{
							g_Target[player.Slot].Add(g_Model[targetplayer.Slot]);

							if (g_bHActWeapon)
							{
								var activeWeapon = targetplayer.Pawn.Value!.WeaponServices?.ActiveWeapon.Value;
								if (activeWeapon != null && activeWeapon.IsValid) g_Target[player.Slot].Add(activeWeapon);
							}

							if (g_bHWeapon)
							{
								var myWeapons = targetplayer.Pawn.Value!.WeaponServices?.MyWeapons;
								if (myWeapons != null)
								{
									foreach (var gun in myWeapons)
									{
										var weapon = gun.Value;
										if (weapon != null) g_Target[player.Slot].Add(weapon);
									}
								}
							}
						}
					});
				}
			});
		}
#nullable enable
		[ConsoleCommand("css_ht", "Allows to hide players and choose the distance")]
		[CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
		public void OnCommandHide(CCSPlayerController? player, CommandInfo command)
#nullable disable
		{
			if (player == null || !player.IsValid) return;
			bool bConsole = command.CallingContext == CommandCallingContext.Console;
			if (!g_bEnable)
			{
				UI.ReplyToCommand(player, bConsole, "Reply.PluginDisabled");
				return;
			}
			int customdistance = -2;
			if (!Int32.TryParse(command.GetArg(1), out customdistance)) customdistance = -2;
			if (customdistance >= 0 && customdistance <= g_iMaxDistance)
			{
				g_bHide[player.Slot] = true;
				g_iDistance[player.Slot] = customdistance;
				SetValue(player);
				if (g_iDistance[player.Slot] == 0) UI.ReplyToCommand(player, bConsole, "Reply.EnableAllMap");
				else UI.ReplyToCommand(player, bConsole, "Reply.Enable", g_iDistance[player.Slot]);
			} else if (customdistance < -2 || customdistance > g_iMaxDistance)
			{
				UI.ReplyToCommand(player, bConsole, "Reply.Wrong", g_iMaxDistance);
			} else if (customdistance == -1)
			{
				g_bHide[player.Slot] = false;
				SetValue(player);
				UI.ReplyToCommand(player, bConsole, "Reply.Disable");
			} else if (customdistance == -2) //Later can be replaced by a menu
			{
				g_bHide[player.Slot] = !g_bHide[player.Slot];
				SetValue(player);
				if (g_bHide[player.Slot])
				{
					if (g_iDistance[player.Slot] == 0) UI.ReplyToCommand(player, bConsole, "Reply.EnableAllMap");
					else UI.ReplyToCommand(player, bConsole, "Reply.Enable", g_iDistance[player.Slot]);
				} else
				{
					UI.ReplyToCommand(player, bConsole, "Reply.Disable");
				}
			}
		}
#nullable enable
		[ConsoleCommand("css_htall", "Allows to hide players and choose the distance")]
		[CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
		public void OnCommandHideAll(CCSPlayerController? player, CommandInfo command)
#nullable disable
		{
			if (player == null || !player.IsValid) return;
			bool bConsole = command.CallingContext == CommandCallingContext.Console;
			if (!g_bEnable)
			{
				UI.ReplyToCommand(player, bConsole, "Reply.PluginDisabled");
				return;
			}
			
			g_bHide[player.Slot] = !g_bHide[player.Slot];
			SetValue(player);
			if (g_bHide[player.Slot])
			{
				if (g_iDistance[player.Slot] == 0) UI.ReplyToCommand(player, bConsole, "Reply.EnableAllMap");
				else UI.ReplyToCommand(player, bConsole, "Reply.Enable", g_iDistance[player.Slot]);
			}
			else
			{
				UI.ReplyToCommand(player, bConsole, "Reply.Disable");
			}
		}
#nullable enable
		void GetValue(CCSPlayerController? player)
#nullable disable
		{
			if (player == null || !player.IsValid) return;
			if (_CP_api != null)
			{
				string sHide = _CP_api.GetClientCookie(player.SteamID.ToString(), "HT_Hide");
				int iHide;
				if (string.IsNullOrEmpty(sHide) || !Int32.TryParse(sHide, out iHide)) iHide = 0;
				if (iHide == 0) g_bHide[player.Slot] = false;
				else g_bHide[player.Slot] = true;

				string sDistance = _CP_api.GetClientCookie(player.SteamID.ToString(), "HT_Distance");
				int iDistance;
				if (string.IsNullOrEmpty(sDistance) || !Int32.TryParse(sDistance, out iDistance)) iDistance = 0;
				if (iDistance <= 0) iDistance = 0;
				else if (iDistance >= g_iMaxDistance) iDistance = g_iMaxDistance;
				g_iDistance[player.Slot] = iDistance;
			}
		}
#nullable enable
		void SetValue(CCSPlayerController? player)
#nullable disable
		{
			if (player == null || !player.IsValid) return;
			if (_CP_api != null)
			{
				if (g_bHide[player.Slot]) _CP_api.SetClientCookie(player.SteamID.ToString(), "HT_Hide", "1");
				else _CP_api.SetClientCookie(player.SteamID.ToString(), "HT_Hide", "0");

				_CP_api.SetClientCookie(player.SteamID.ToString(), "HT_Distance", g_iDistance[player.Slot].ToString());
			}
		}

#nullable enable
		void SetModel(CCSPlayerController? player, string sModelPath)
#nullable disable
		{
			if (player == null || !player.IsValid || !player.Pawn.Value.IsValid || player.Pawn.Value.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
			if (g_Model[player.Slot] != null && g_Model[player.Slot].IsValid)
			{
				g_Model[player.Slot].Remove();
			}
			g_Model[player.Slot] = null;

			var entity = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic")!;
			if (entity == null || !entity.IsValid) return;
			
			entity.Spawnflags = 256;
			entity!.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags = (uint)(entity.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags & ~(1 << 2));
			//entity.DispatchSpawn();
			entity.SetModel(sModelPath);
			entity.AcceptInput("FollowEntity", player.Pawn.Value, null, "!activator");

			g_Model[player.Slot] = entity;
		}

		void RemoveModel(CCSPlayerController? player)
		{
			if (player == null || !player.IsValid || !player.Pawn.Value.IsValid) return;
			if (g_Model[player.Slot] != null && g_Model[player.Slot].IsValid)
			{
				g_Model[player.Slot].Remove();
			}
			g_Model[player.Slot] = null;
			ForceFullUpdate(player);
			player.Pawn.Value.Render = Color.FromArgb(255, 255, 255, 255);
			Utilities.SetStateChanged(player.Pawn.Value, "CBaseModelEntity", "m_clrRender");
		}

		void CreateTimer()
		{
			CloseTimer();
			g_Timer = new CounterStrikeSharp.API.Modules.Timers.Timer(TIMERTIME, OnTimer, TimerFlags.REPEAT);
		}

		void CloseTimer()
		{
			if (g_Timer != null)
			{
				g_Timer.Kill();
				g_Timer = null;
			}
		}

		float Distance(Vector point1, Vector point2)
		{
			float dx = point2.X - point1.X;
			float dy = point2.Y - point1.Y;
			float dz = point2.Z - point1.Z;

			return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}
	}
}
