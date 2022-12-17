﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Dynamic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using EvenBetterRNG;
using GenericModConfigMenu;
using Microsoft.VisualBasic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Tools;
using static ImmersiveWeathers.IIWAPI;

// TODO: When SDV 1.6 releases, check for compatibility with new weather flags: weatherForTomorrow etc are switching to strings instead of integers. See https://stardewvalleywiki.com/Modding:Migrate_to_Stardew_Valley_1.6 for more information.

// TODO: Write custom log messages for weather changes <----- v0.7.0
// TODO: Add mod config menu (make sure to reload any changes from player) <----- v0.8.0
// TODO: Improve comments <----- v1.0.0
// TODO: Simplify trace messages

namespace ImmersiveWeathers
{
    // ----------
    // MAIN CLASS
    // ----------
    // SMAPI loads this on launch
    internal sealed class IWFramework : Mod
    {
        // --------
        // SEND API
        // --------
        // Tells SMAPI how to get an API copy for each mod
        public override object GetApi(IModInfo mod)
        {
            return new IIWAPI();
        }

        // --------------------
        // FIELDS AND VARIABLES
        // --------------------
        // SMAPI initialises fields on launch
        // Define config fields and variables
        private static ModConfig s_config;

        // Define PRNG field for use by sister mods
        internal static Random s_pRNG = new();

        // How to track sister mods
        internal static TrackSisters s_trackSisters = new();

        // Define field for handling event calls
        internal static EventManager s_eventManager = new();

        // -----------
        // MAIN METHOD
        // -----------
        // SMAPI calls this on launch
        public override void Entry(IModHelper helper)
        {
            // ---------
            // GAME LOAD
            // ---------
            // When game loaded, initialised variables
            Helper.Events.GameLoop.GameLaunched += GameLoop_Initialize;
            // Also set up internal event handler
            s_eventManager.SendToFramework += ReceiveEvent;

            // When day begins, generate a weather forecast
            Helper.Events.GameLoop.DayStarted += StartDay_WeatherForecaster;
        }

        // --------------------
        // INITIALIZE VARIABLES
        // --------------------
        // Initialize variables on game launch
        private void GameLoop_Initialize(object sender, GameLaunchedEventArgs e)
        {
            // Tell SMAPI where to grab config options
            s_config = Helper.ReadConfig<ModConfig>();
            if (s_config.PrintToTerminal)
                Monitor.Log("Terminal logging enabled.", LogLevel.Info);
            if (s_config.PrintHUDMessage)
                Monitor.Log("HUD messages enabled.", LogLevel.Trace);

            // Grab PRNG from EvenBetterRNG Mod API, if present
            IEvenBetterRNGAPI eBRNG = Helper.ModRegistry.GetApi<IEvenBetterRNGAPI>("pepoluan.EvenBetterRNG");
            if (eBRNG != null)
            {
                s_pRNG = eBRNG.GetNewRandom();
            }
            // Grab GenericModConfigMenu API
            IGenericModConfigMenuApi gMCM = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gMCM != null)
            {
                GMCMHandler.Register(s_config, gMCM, ModManifest, Helper);
            }
            // Make a list of all sister mods that are present so can delay console logging until all have reported in
            if (Helper.ModRegistry.IsLoaded("MsBontle.ClimateControl"))
            {
                s_trackSisters.ClimateControlPresent = true;
                Monitor.Log("ClimateControl detected. Enabling integration...", LogLevel.Trace);
            }
        }
        // ----------------
        // FORECAST WEATHER
        // ----------------
        // Forecast the weather once the day has started and all sister mods have reported in
        private void StartDay_WeatherForecaster(object sender, DayStartedEventArgs e)
        {
            // Check if waiting for any sister mods first
            Monitor.Log("Day started. Waiting for sister mods before continuing...", LogLevel.Trace);
            bool continueForecast;
            continueForecast = CheckForSistersReady();
            if (continueForecast)
            {
                Monitor.Log("No sister mods were loaded. Will only provide player with weather predictions.", LogLevel.Trace);
                ForecastWeather();
            }
        }

        private static bool CheckForSistersReady()
        {
            // Wait until all sister mods have reported in
            bool continueForecast = true;
            foreach (PropertyInfo property in typeof(MorningUpdate).GetProperties())
            {
                if ((bool)property.GetValue(s_trackSisters.MorningUpdate) == false)
                {
                    continueForecast = false;
                    break;
                }
            }
            return continueForecast;
        }

        private void ForecastWeather()
        {
            // Grab information about the game's current weather state
            WeatherUtils.WeatherState weatherForecast = WeatherUtils.PopulateWeather.Populate();

            // Print appropriate weather update to SMAPI terminal
            string weatherString = WeatherMan.Predict(weatherForecast);
            BroadCast(weatherString);
            
            // Set flags back to false
            foreach (PropertyInfo property in typeof(MorningUpdate).GetProperties())
            {
                property.SetValue(s_trackSisters.MorningUpdate, false);
            }
        }

        // ---------
        // BROADCAST
        // ---------
        // Broadcast updates to SMAPI terminal
        private void BroadCast(string terminalUpdate)
        {
            if (s_config.PrintToTerminal)
            {
                Monitor.Log($"{terminalUpdate}", LogLevel.Info);
            }
            if (s_config.PrintHUDMessage)
            {
                Monitor.Log($"Broadcasting the following message to player HUD: \"{terminalUpdate}\"", LogLevel.Trace);
                Game1.addHUDMessage(new HUDMessage($"{terminalUpdate}", ""));
            }
        }

        // ------------------
        // INTERPRET MESSAGES
        // ------------------
        // Sort messages from sister mods into expected calls
        private void ReceiveEvent(object sender, EventContainer e)
        {
            switch (e.Message.MessageType)
            {
                case MessageTypes.saveLoaded:
                    SaveLoadedMessages(e);
                    break;
                case MessageTypes.titleReturned:
                    break;
                case MessageTypes.dayStarted:
                    DayLoadedMessage(e);
                    if (CheckForSistersReady() && (s_config.PrintToTerminal || s_config.PrintHUDMessage))
                        Monitor.Log("All sister mods reported in. Preparing to broadcast weather predictions...", LogLevel.Trace);
                        ForecastWeather();
                    break;
                default:
                    break;
            }
        }

        // Sort calls into sister mods
        private void SaveLoadedMessages(EventContainer e)
        {
            switch (e.Message.SisterMod)
            {
                case SisterMods.ClimateControl:
                    if ((!s_trackSisters.ClimateControl.ModelLoaded) || (s_trackSisters.ClimateControl.ModelType != e.Message.ModelType))
                    {
                        Monitor.Log("Permission granted: model not yet cached or model is different from the current cache.", LogLevel.Trace);
                        s_trackSisters.ClimateControl.ModelLoaded = true;
                        s_trackSisters.ClimateControl.ModelType = e.Message.ModelType;
                        e.Response.GoAheadToLoad = true;
                    }
                    else
                        Monitor.Log("Request denied: model is already cached.", LogLevel.Trace);
                    break;
                default:
                    break;
            }
        }

        private void DayLoadedMessage(EventContainer e)
        {
            switch (e.Message.SisterMod)
            {
                case SisterMods.ClimateControl:
                    if (e.Message.CouldChange)
                    {
                        Monitor.Log($"Acknowledged: weather successfully changed to {e.Message.WeatherType}.", LogLevel.Trace);
                        s_trackSisters.ClimateControl.ChangedWeather = true;
                        s_trackSisters.ClimateControl.ChangedToType = e.Message.WeatherType;
                    }
                    else
                    {
                        Monitor.Log($"Acknowledged: failed to change weather.", LogLevel.Trace);
                        s_trackSisters.ClimateControl.ChangedWeather = false;
                    }
                    s_trackSisters.MorningUpdate.ClimateControl = true;
                    e.Response.Acknowledged = true;
                    break;
                default:
                    break;
            }
        }
    }
}