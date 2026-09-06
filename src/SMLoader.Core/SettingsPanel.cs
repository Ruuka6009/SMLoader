namespace SMLoader.Core;

/// <summary>
/// A standalone settings screen, built from the game's own JSON GUI system and
/// driven entirely by <see cref="SettingsRegistry"/> - so any mod that declares
/// a setting gets a row without writing UI of its own.
/// </summary>
/// <remarks>
/// An earlier attempt injected a sixth tab into the game's Options screen. That
/// does not work: the option tabs are built in C++ at 0x3f4c30 from hardcoded
/// string literals, and the engine lays out and binds only the tabs it knows,
/// so an injected widget is orphaned and painted over. A real tab would mean
/// recreating MyGUI widget construction and event binding from outside the
/// engine, which is far more fragile than this.
/// </remarks>
internal static class SettingsPanel
{
    public static void Install()
    {
        // CreativePlayer, not CreativeGame. CreativeGame.lua ends by deriving
        // ClassicCreativeGame / CreativeCustomGame / CreativeTerrainGame, and a
        // world runs one of those - assigning to the base class never reaches
        // them, which is the same trap the chat commands fell into.
        // CreativePlayer has no such subclasses and its client_onUpdate is
        // known to run every frame, since noclip movement rides on it.
        ScriptPatcher.Register("SMLoader", "CreativePlayer.lua",
            context => context.Append(PanelLua));
    }

    /// <summary>
    /// Appended to CreativePlayer.lua. Only uses globals the script sandbox
    /// actually provides - calling a stripped one aborts the whole chunk and the
    /// engine then fails to load the script.
    /// </summary>
    private const string PanelLua = """
        if not CreativePlayer.smloaderPanelPatched then
        CreativePlayer.smloaderPanelPatched = true

        local SMLOADER_PANEL_KEY = 0x79   -- F10

        -- Edge state kept as script locals, not on self.cl. More than one
        -- object runs this update, and per-instance state let them disagree -
        -- one opened the panel and another closed it in the same press, so it
        -- took two presses to appear.
        local smloader_panelKeyWasDown = false
        local smloader_panelCooldown = 0
        local SMLOADER_MAX_ROWS = 32

        -- Geometry is kept inside the background skin: the first version had
        -- rows running past the right edge of the panel.
        local SMLOADER_PANEL_W = 560

        local function smloader_addRow( children, index, label, value, kind, y )
            children[#children + 1] =
                { Type = "EditBox", Skin = "TextBox", Static = true, Childs = {},
                  Name = "SMLabel" .. index, Caption = label,
                  FontName = "SM_Text", TextAlign = "Left",
                  x = 30, y = y, width = 250, height = 32 }

            if kind == "number" then
                -- Stepper rather than a slider: jsonGui only surfaces onClick,
                -- so a ScrollBar would render but never report a change.
                children[#children + 1] =
                    { Type = "Button", Skin = "SettingsButton", Childs = {},
                      Name = "SMMinus" .. index, Caption = "-",
                      FontName = "SM_Text", TextAlign = "Center",
                      onClick = "cl_smloader_dec" .. index,
                      x = 300, y = y, width = 44, height = 32 }

                children[#children + 1] =
                    { Type = "EditBox", Skin = "TextBox", Static = true, Childs = {},
                      Name = "SMValue" .. index, Caption = value,
                      FontName = "SM_Text", TextAlign = "Center",
                      x = 350, y = y, width = 130, height = 32 }

                children[#children + 1] =
                    { Type = "Button", Skin = "SettingsButton", Childs = {},
                      Name = "SMPlus" .. index, Caption = "+",
                      FontName = "SM_Text", TextAlign = "Center",
                      onClick = "cl_smloader_inc" .. index,
                      x = 486, y = y, width = 44, height = 32 }
            else
                children[#children + 1] =
                    { Type = "Button", Skin = "SettingsButton", Childs = {},
                      Name = "SMValue" .. index, Caption = value,
                      FontName = "SM_Text", TextAlign = "Center",
                      onClick = "cl_smloader_row" .. index,
                      x = 300, y = y, width = 230, height = 32 }
            end
        end

        local function smloader_buildTree( self )
            local children = {}

            children[#children + 1] =
                { Type = "EditBox", Skin = "TextBox", Static = true, Childs = {},
                  Name = "SMTitle", Caption = "SMLOADER SETTINGS",
                  FontName = "SM_HeaderLarge_Medium", TextAlign = "Center",
                  x = 30, y = 22, width = SMLOADER_PANEL_W - 60, height = 40 }

            -- Every smloader entry is checked, not just settingCount. If seeding
            -- failed - no lua_setfenv slot, which the shim logs - the table is nil
            -- or partial, and calling through it throws inside the engine pcall
            -- where the panel just silently does nothing.
            local ok = smloader and smloader.settingCount and smloader.settingAt

            local count = 0
            if ok then
                count = smloader.settingCount()
            elseif not SMLOADER_REPORTED_MISSING then
                SMLOADER_REPORTED_MISSING = true
                if smloader and smloader.logMessage then
                    smloader.logMessage( "the smloader table is missing from this script; "
                        .. "the settings panel cannot list anything" )
                else
                    print( "[SMLoader] the smloader table never reached the settings panel" )
                end
            end
            if count > SMLOADER_MAX_ROWS then
                count = SMLOADER_MAX_ROWS
            end

            local y = 80
            for i = 1, count do
                local modName, key, label, kind, value = smloader.settingAt( i )
                modName = modName or "?"
                label = label or key or "?"
                local caption = value
                if self.cl.smloaderAwaitKey == i then
                    caption = "PRESS A KEY..."
                end
                smloader_addRow( children, i, label .. "  (" .. modName .. ")", caption, kind, y )
                y = y + 38
            end

            if count == 0 then
                children[#children + 1] =
                    { Type = "EditBox", Skin = "TextBox", Static = true, Childs = {},
                      Name = "SMEmpty", Caption = "No mod settings declared",
                      FontName = "SM_Text", TextAlign = "Center",
                      x = 30, y = y, width = SMLOADER_PANEL_W - 60, height = 32 }
                y = y + 38
            end

            children[#children + 1] =
                { Type = "Button", Skin = "PrimaryButton", Childs = {},
                  Name = "SMClose", Caption = "CLOSE",
                  FontName = "SM_Text", TextAlign = "Center",
                  onClick = "cl_smloader_close",
                  x = ( SMLOADER_PANEL_W - 140 ) / 2, y = y + 16, width = 140, height = 34 }

            return { Anchor = "Center", Name = "SMLoaderPanel", Type = "Widget",
                     Skin = "BackgroundDarkRoundedUpperRight", InheritsPick = true,
                     NeedKey = false, NeedMouse = false,
                     x = 0, y = 0, width = SMLOADER_PANEL_W, height = y + 74,
                     Childs = children }
        end

        function CreativePlayer.cl_smloader_openPanel( self )
            if not self.cl then
                return
            end
            self:cl_smloader_close()
            self.cl.smloaderPanel = sm.jsonGui.createGui( { isInteractive = true, needsCursor = true } )
            self.cl.smloaderPanel:render( smloader_buildTree( self ) )
        end

        -- Re-render onto the EXISTING gui. Closing and recreating it to show a
        -- new value is what made the panel vanish the moment a value changed.
        function CreativePlayer.cl_smloader_refresh( self )
            if self.cl and self.cl.smloaderPanel then
                self.cl.smloaderPanel:render( smloader_buildTree( self ) )
            else
                self:cl_smloader_openPanel()
            end
        end

        function CreativePlayer.cl_smloader_close( self )
            if self.cl and self.cl.smloaderPanel then
                self.cl.smloaderPanel:close()
                self.cl.smloaderPanel = nil
            end
        end

        -- One handler per row: a JSON gui binds onClick to a named method, so
        -- the row index has to be baked into the name.
        for i = 1, SMLOADER_MAX_ROWS do
            CreativePlayer["cl_smloader_row" .. i] = function( self )
                local modName, key, label, kind, value = smloader.settingAt( i )
                if not kind then
                    return
                end

                if kind == "key" then
                    -- client_onUpdate polls for the next keypress.
                    self.cl.smloaderAwaitKey = i
                    self:cl_smloader_refresh()

                elseif kind == "toggle" then
                    smloader.settingSet( i, value ~= "On" )
                    self:cl_smloader_refresh()

                end
            end

            local function smloader_step( self, index, delta )
                local _, _, _, _, value = smloader.settingAt( index )
                smloader.settingSet( index, ( tonumber( value ) or 0 ) + delta )
                self:cl_smloader_refresh()
            end

            CreativePlayer["cl_smloader_inc" .. i] = function( self )
                smloader_step( self, i, 0.05 )
            end

            CreativePlayer["cl_smloader_dec" .. i] = function( self )
                smloader_step( self, i, -0.05 )
            end
        end

        local smloader_panelOriginalUpdate = CreativePlayer.client_onUpdate

        CreativePlayer.client_onUpdate = function( self, dt, ... )
            -- self.cl does not exist until client_onCreate has run, and this
            -- fires before that. Indexing it threw every frame, which aborted
            -- the whole client update chain - taking the player's own update,
            -- and so noclip movement, down with it.
            --
            -- The pcall is deliberate belt-and-braces: nothing the loader adds
            -- to a game script should ever be able to break the game.
            if not self.cl then
                if smloader and smloader.logMessage and not SMLOADER_PANEL_NOCL then
                    SMLOADER_PANEL_NOCL = true
                    smloader.logMessage( "client_onUpdate running but self.cl is nil" )
                end
            end

            if self.cl and smloader and smloader.isKeyDown then
                if not SMLOADER_PANEL_ALIVE then
                    SMLOADER_PANEL_ALIVE = true
                    smloader.logMessage( "panel update hook is live; watching for the open key" )
                end

                local ok, err = pcall( function()
                    if smloader_panelCooldown > 0 then
                        smloader_panelCooldown = smloader_panelCooldown - ( dt or 0.016 )
                    end

                    local down = smloader.isKeyDown( SMLOADER_PANEL_KEY )
                    if down and not smloader_panelKeyWasDown and smloader_panelCooldown <= 0 then
                        smloader_panelCooldown = 0.3
                        if self.cl.smloaderPanel then
                            self:cl_smloader_close()
                        else
                            self:cl_smloader_openPanel()
                        end
                    end
                    smloader_panelKeyWasDown = down

                    if self.cl.smloaderAwaitKey and smloader.pollAnyKey then
                        local pressed = smloader.pollAnyKey()
                        if pressed and pressed > 0 then
                            smloader.settingSet( self.cl.smloaderAwaitKey, pressed )
                            self.cl.smloaderAwaitKey = nil
                            self:cl_smloader_openPanel()
                        end
                    end
                end )

                if not ok and not SMLOADER_PANEL_ERR then
                    SMLOADER_PANEL_ERR = true
                    smloader.logMessage( "panel failed: " .. tostring( err ) )
                end
            end

            if smloader_panelOriginalUpdate then
                return smloader_panelOriginalUpdate( self, dt, ... )
            end
        end

        end -- CreativePlayer.smloaderPanelPatched
        """;
}
