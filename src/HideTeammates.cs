using ClientPrefsAPI;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Capabilities;
using static CounterStrikeSharp.API.Core.Listeners;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Commands.Targeting;

namespace CS2_HideTeammates
{
	public class HideTeammates : BasePlugin
	{
		static IClientPrefsAPI _CP_api;
		bool g_bEnable = true;
		int g_iMaxDistance = 8000;
		bool[] g_bHide = new bool[65];
		int[] g_iDistance = new int[65];

		//Client Crash Fix From: https://github.com/qstage/CS2-HidePlayers
		private static readonly MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState> StateTransition = new(GameData.GetSignature("StateTransition"));
		private readonly INetworkServerService networkServerService = new();
		private readonly CSPlayerState[] g_PlayerState = new CSPlayerState[65];

		public FakeConVar<bool> Cvar_Enable = new("css_ht_enabled", "Disabled/enabled [0/1]", true, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<bool>(false, true));
		public FakeConVar<int> Cvar_MaxDistance = new("css_ht_maximum", "The maximum distance a player can choose [1000-8000]", 8000, flags: ConVarFlags.FCVAR_NOTIFY, new RangeValidator<int>(1000, 8000));
		public override string ModuleName => "Hide Teammates";
		public override string ModuleDescription => "A plugin that can !hide with individual distances";
		public override string ModuleAuthor => "DarkerZ [RUS]";
		public override string ModuleVersion => "1.DZ.2";
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
			RegisterListener<CheckTransmit>(OnTransmit);
		}

		public override void Unload(bool hotReload)
		{
			StateTransition.Unhook(Hook_StateTransition, HookMode.Post);
			DeregisterEventHandler<EventPlayerConnectFull>(OnEventPlayerConnectFull);
			DeregisterEventHandler<EventPlayerDisconnect>(OnEventPlayerDisconnect);
			RemoveListener<CheckTransmit>(OnTransmit);
		}

		private void ForceFullUpdate(CCSPlayerController? player)
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

		HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;
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

		void OnTransmit(CCheckTransmitInfoList infoList)
		{
			if (!g_bEnable) return;
			foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
			{
				if (player == null || !player.IsValid || !player.Pawn.IsValid || player.Connected != PlayerConnectedState.PlayerConnected) continue;

				foreach (CCSPlayerController target in Utilities.GetPlayers())
				{
					if (target == null || target.IsHLTV || target.Slot == player.Slot)
						continue;

					var targetpawn = target.PlayerPawn.Value!;

					if (player.Pawn.Value?.As<CCSPlayerPawnBase>().PlayerState == CSPlayerState.STATE_OBSERVER_MODE)
						continue;

					if ((LifeState_t)targetpawn.LifeState != LifeState_t.LIFE_ALIVE)
					{
						info.TransmitEntities.Remove(targetpawn);
						continue;
					}

					if (target.Team == player.Team)
					{
						if (g_iDistance[player.Slot] == 0) info.TransmitEntities.Remove(targetpawn);
						else
						{
							if (Distance(target.PlayerPawn.Value?.AbsOrigin, player.PlayerPawn.Value?.AbsOrigin) <= g_iDistance[player.Slot])
							{
								//Console.WriteLine($"Player: {player.Slot} Target: {target.Slot} Distance: {(float)(Distance(target.PlayerPawn.Value?.AbsOrigin, player.PlayerPawn.Value?.AbsOrigin))}");
								info.TransmitEntities.Remove(targetpawn);
							}
						}
					}
				}
			}
		}

		[ConsoleCommand("css_ht", "Allows to hide players and choose the distance")]
		[CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
		public void OnCommandHide(CCSPlayerController? player, CommandInfo command)
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

		[ConsoleCommand("css_htall", "Allows to hide players and choose the distance")]
		[CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
		public void OnCommandHideAll(CCSPlayerController? player, CommandInfo command)
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

		async void GetValue(CCSPlayerController? player)
		{
			if (player == null || !player.IsValid) return;
			if (_CP_api != null)
			{
				string sHide = await _CP_api.GetClientCookie(player.SteamID.ToString(), "HT_Hide");
				int iHide;
				if (string.IsNullOrEmpty(sHide) || !Int32.TryParse(sHide, out iHide)) iHide = 0;
				if (iHide == 0) g_bHide[player.Slot] = false;
				else g_bHide[player.Slot] = true;

				string sDistance = await _CP_api.GetClientCookie(player.SteamID.ToString(), "HT_Distance");
				int iDistance;
				if (string.IsNullOrEmpty(sDistance) || !Int32.TryParse(sDistance, out iDistance)) iDistance = 0;
				if (iDistance <= 0) iDistance = 0;
				else if (iDistance >= g_iMaxDistance) iDistance = g_iMaxDistance;
				g_iDistance[player.Slot] = iDistance;
			}
		}

		async void SetValue(CCSPlayerController? player)
		{
			if (player == null || !player.IsValid) return;
			if (_CP_api != null)
			{
				if (g_bHide[player.Slot]) await _CP_api.SetClientCookie(player.SteamID.ToString(), "HT_Hide", "1");
				else await _CP_api.SetClientCookie(player.SteamID.ToString(), "HT_Hide", "0");

				await _CP_api.SetClientCookie(player.SteamID.ToString(), "HT_Distance", g_iDistance[player.Slot].ToString());
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
