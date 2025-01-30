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
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Commands.Targeting;

namespace CS2_HideTeammates
{
	public class HideTeammates : BasePlugin
	{
		float TIMERTIME = 0.3f;
		static IClientPrefsAPI _CP_api;
		bool g_bEnable = true;
		int g_iMaxDistance = 8000;
		bool[] g_bHide = new bool[65];
		int[] g_iDistance = new int[65];
		bool[] g_bRMB = new bool[65];
		List<CCSPlayerController>[] g_Target = new List<CCSPlayerController>[65];
		CounterStrikeSharp.API.Modules.Timers.Timer g_Timer;

		//Client Crash Fix From: https://github.com/qstage/CS2-HidePlayers
		private static readonly MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState> StateTransition = new(GameData.GetSignature("StateTransition"));
		private readonly INetworkServerService networkServerService = new();

		static readonly MemoryFunctionVoid<long, long, long, long> CopyExistingEntity_missingFunc = new(GameData.GetSignature("CopyExistingEntity_missing"));

		public FakeConVar<bool> Cvar_Enable = new("css_ht_enabled", "Disabled/enabled [0/1]", true, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<bool>(false, true));
		public FakeConVar<int> Cvar_MaxDistance = new("css_ht_maximum", "The maximum distance a player can choose [1000-8000]", 8000, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<int>(1000, 8000));
		public override string ModuleName => "Hide Teammates";
		public override string ModuleDescription => "A plugin that can !hide with individual distances";
		public override string ModuleAuthor => "DarkerZ [RUS]";
		public override string ModuleVersion => "1.DZ.4test3";
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
			StateTransition.Hook(Hook_StateTransition, HookMode.Pre);
			for (int i = 0; i < 65; i++) g_Target[i] = new List<CCSPlayerController>();
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

			RegisterFakeConVars(typeof(ConVar));

			RegisterEventHandler<EventPlayerConnectFull>(OnEventPlayerConnectFull);
			RegisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
			RegisterListener<OnMapStart>(OnMapStart_Listener);
			RegisterListener<OnMapEnd>(OnMapEnd_Listener);
			RegisterListener<CheckTransmit>(OnTransmit);
			RegisterListener<OnTick>(OnOnTick_Listener);

			VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(Hook_TakeDamageOld, HookMode.Pre);

			CreateTimer();
		}

		public override void Unload(bool hotReload)
		{
			StateTransition.Unhook(Hook_StateTransition, HookMode.Pre);
			DeregisterEventHandler<EventPlayerConnectFull>(OnEventPlayerConnectFull);
			DeregisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
			RemoveListener<OnMapStart>(OnMapStart_Listener);
			RemoveListener<OnMapEnd>(OnMapEnd_Listener);
			RemoveListener<CheckTransmit>(OnTransmit);
			RemoveListener<OnTick>(OnOnTick_Listener);

			VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(Hook_TakeDamageOld, HookMode.Pre);

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

		private HookResult Hook_TakeDamageOld(DynamicHook hook)
		{
			var victim = hook.GetParam<CEntityInstance>(0);
			var info = hook.GetParam<CTakeDamageInfo>(1);

			if (victim.DesignerName != "player") return HookResult.Continue;

			var pawn = victim.As<CCSPlayerPawn>();

			if (pawn == null || !pawn.IsValid) return HookResult.Continue;

			if (info.DamageFlags.HasFlag(TakeDamageFlags_t.DFLAG_FORCE_DEATH))
			{
				info.DamageFlags &= ~TakeDamageFlags_t.DFLAG_FORCE_DEATH;
				StateTransition.Invoke(pawn, CSPlayerState.STATE_WELCOME);

				return HookResult.Changed;
			}

			return HookResult.Continue;
		}

		private HookResult Hook_StateTransition(DynamicHook hook)
		{
			var player = hook.GetParam<CCSPlayerPawn>(0).OriginalController.Value;
			var state = hook.GetParam<CSPlayerState>(1);

			if (player is null) return HookResult.Continue;

			if (state != player.Pawn.Value?.As<CCSPlayerPawnBase>().PlayerState)
			{
				ForceFullUpdate(player);
			}


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
				if (player == null || !player.IsValid || !player.Pawn.IsValid || player.Pawn.Value == null) continue;

				foreach (CCSPlayerController targetPlayer in g_Target[player.Slot].ToList())
				{
					if (targetPlayer == null || !targetPlayer.IsValid) continue;
					var targetPawn = targetPlayer.PlayerPawn.Value;
					if (targetPawn == null || !targetPawn.IsValid) continue;

					if ((targetPawn.LifeState != (byte)LifeState_t.LIFE_DEAD || targetPawn.LifeState != (byte)LifeState_t.LIFE_DYING) && player.Pawn.Value?.As<CCSPlayerPawnBase>().PlayerState == CSPlayerState.STATE_OBSERVER_MODE) continue;

					info.TransmitEntities.Remove(targetPawn.Index);
				}
			}
		}

		void OnTimer()
		{
			if (!g_bEnable) return;
			Utilities.GetPlayers().Where(p => p.IsValid && p.Pawn.IsValid && p.Pawn.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE).ToList().ForEach(player =>
			{
				g_Target[player.Slot].Clear();
				if (g_bHide[player.Slot] && !g_bRMB[player.Slot])
				{
					Utilities.GetPlayers().Where(target => target != null && target.IsValid && target.Pawn.IsValid && target.Slot != player.Slot && target.Team == player.Team).ToList().ForEach(targetplayer =>
					{
						if (g_iDistance[player.Slot] == 0) g_Target[player.Slot].Add(targetplayer);
						else
						{
							if (Distance(targetplayer.Pawn.Value?.AbsOrigin, player.Pawn.Value?.AbsOrigin) <= g_iDistance[player.Slot])
							{
								//Console.WriteLine($"Player: {player.Slot} Target: {targetplayer.Slot} Distance: {(float)(Distance(targetplayer.Pawn.Value?.AbsOrigin, player.Pawn.Value?.AbsOrigin))}");
								g_Target[player.Slot].Add(targetplayer);
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
