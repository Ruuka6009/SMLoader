using System.Numerics;
using System.Runtime.InteropServices;
using SMLoader.Api;
using SMLoader.Api.Lua;

namespace NoclipMod;

/// <summary>
/// Noclip for creative mode. No game file is modified: CreativePlayer.lua and
/// CreativeGame.lua are rewritten in memory as the engine compiles them.
///
/// Movement lives in Lua because that is where the sm.* API is. Key polling and
/// all shared state live here, because the game's fixed action bindings cannot
/// express an arbitrary hotkey - and because each game script gets its own
/// environment, so a Lua global cannot carry state from one script to another.
/// </summary>
public sealed class NoclipMod : IMod
{
    private const string KeyOption = "noclipKey";
    private const string TeleportOption = "teleportMovement";
    private const string SpeedOption = "noclipSpeed";
    private const int DefaultKey = 0x56;     // VK_V (also the game's Camera toggle)
    private const double DefaultSpeed = 0.4; // metres per tick (40 ticks/s)

    public string Name => "NoclipMod";

    public string Version => "1.1.0";

    private bool _enabled;

    // Cached because Lua asks for these constantly - noclipKey every fixed
    // tick at 40 Hz, noclipSpeed every rendered frame while flying - and each
    // Config.Get was a lock plus a full System.Text.Json converter dispatch.
    // Kept current from IModSettings.Changed rather than re-read.
    private int _noclipKey = DefaultKey;
    private double _noclipSpeed = DefaultSpeed;
    private nint _character;        // server-side character
    private nint _clientCharacter;  // client-side copy; this is the one rendered

    // Offsets recovered from the game's own Lua bindings in ScrapMechanic.exe:
    //   getVelocity      -> movsd xmm0,[rcx+0xA0] ; mov ecx,[rcx+0xA8]
    //   getWorldPosition -> [[character+0xB68]+0x10]+0x40
    // Build-specific; every read is bounds-checked before use.
    private const int PositionOffsetCache = 0xA0;
    private const int PositionChainFirst = 0xB68;
    private const int PositionChainSecond = 0x10;
    private const int PositionOffset = 0x40;

    // Measured, not guessed: while flying straight up at 30 m/s, body+0x048 and
    // body+0x348 both stepped +30.000/s in lockstep with the real position,
    // while body+0x1C8 tracked the same curve a constant 2.237 behind. That
    // last one is a smoothed copy the renderer draws - leaving it to catch up
    // on its own is what looked like jitter, so all three are written.
    // Measured while stationary: the altitude appears at char +0xA8, +0x218,
    // +0xCB8, +0xD54 and body +0x48, +0x1C8, +0x348, +0x4C8 - eight copies of
    // the same z. Writing only some of them let the engine recompute from the
    // rest, and the two sets diverged further every frame. That divergence is
    // the jitter, and it is why it grew the longer you stood still.
    // Each is the z of a vec3, so the bases sit 8 bytes earlier.
    private static readonly int[] CharacterMirrors = { 0xA0, 0x210, 0xCB0, 0xD4C };
    // body+0x1C0 is deliberately NOT written. It measured a constant 2.237
    // below the true position, which looks like a separate origin (eye vs feet)
    // rather than lag - flattening it made the engine reconcile on release and
    // pop the player upward by about that much. With the body pinned immovable
    // nothing can drift, so it is left to the engine.
    private static readonly int[] BodyMirrors = { 0x40, 0x340, 0x4C0 };

    // body+0x1C0 is the copy the renderer draws, and it keeps its own constant
    // offset from the true position. It has to be written or nothing moves on
    // screen - with the body immovable the engine never refreshes it - but
    // writing it flat pops the player upward on release. So its offset is
    // captured when the character is bound and preserved on every write.
    // The render copy. Writing only its z made vertical movement show on screen
    // while horizontal did not, so the whole vec3 is written - this is the form
    // that demonstrably works. Note +0x1C0/+0x1C4 read as implausible floats,
    // so they may not be coordinates; revisit before trusting this on another
    // build of the game.
    private const int RenderMirror = 0x1C0;
    private Vector3 _renderOffset;

    //   isOnGround -> movzx edx, byte [rcx+0x8E8]
    // Holding this true makes the controller treat the character as standing on
    // something, so it never integrates gravity - which is what actually stops
    // the velocity build-up rather than clearing it after the fact.
    private const int GroundedOffset = 0x8E8;

    // Immediately after the grounded flag, and measured climbing by exactly
    // 1.000 per second for as long as the character is off the ground: this is
    // the airborne timer. Fall force is derived from it, which is why the pull
    // got stronger the longer noclip stayed on, and why clearing a velocity
    // after the fact never held - the timer simply regenerated it.
    private const int AirborneTimerOffset = 0x8EC;

    //   getImmovable -> movzx reg, byte [character+0xCC9]
    //   isTumbling   -> cmp   byte [character+0x932]
    // Immovable is the engine's own "physics must not move this body" flag.
    // Setting it through Lua clearly was not sticking, so it is pinned here
    // every frame instead - the oscillation happens within a frame, so the
    // flag has to be true when the controller runs, not merely set once.
    private const int ImmovableOffset = 0xCC9;
    private const int TumblingOffset = 0x932;

    public void OnLoad(IModHost host)
    {
        // Declared rather than just stored: SMLoader persists these and shows
        // them in the shared settings panel, so this mod needs no UI of its own.
        host.Settings.Declare<int>(new ModSetting(
            KeyOption, "Noclip toggle", SettingKind.Key, DefaultKey)
        {
            Description = "Key that turns noclip on and off",
        });

        host.Settings.Declare<double>(new ModSetting(
            SpeedOption, "Noclip speed", SettingKind.Number, DefaultSpeed)
        {
            Description = "Metres per tick while flying",
            Minimum = 0.05,
            Maximum = 5.0,
            Step = 0.05,
        });

        _noclipKey = host.Config.Get(KeyOption, DefaultKey);
        _noclipSpeed = host.Config.Get(SpeedOption, DefaultSpeed);

        host.Settings.Changed += key =>
        {
            if (string.Equals(key, KeyOption, StringComparison.OrdinalIgnoreCase))
                _noclipKey = host.Config.Get(KeyOption, DefaultKey);
            else if (string.Equals(key, SpeedOption, StringComparison.OrdinalIgnoreCase))
                _noclipSpeed = host.Config.Get(SpeedOption, DefaultSpeed);
        };

        host.Log($"toggle key 0x{_noclipKey:X2}, speed {_noclipSpeed}");

        host.AddLuaFunction("isKeyDown", lua =>
        {
            // GetAsyncKeyState is global to the machine, so without this the
            // keys kept firing while the game was alt-tabbed - noclip would
            // toggle and drift while you were in another window.
            lua.Push(IsGameFocused() && (GetAsyncKeyState((int)lua.ToInteger(1)) & 0x8000) != 0);
            return 1;
        });

        host.AddLuaFunction("isNoclip", lua =>
        {
            lua.Push(_enabled);
            return 1;
        });

        host.AddLuaFunction("setNoclip", lua =>
        {
            _enabled = lua.ToBoolean(1);

            if (!_enabled)
            {
                // Hand the body back to the engine, or it stays frozen.
                foreach (nint character in new[] { _character, _clientCharacter })
                {
                    if (character == 0)
                        continue;

                    host.Memory.Write<byte>(character + ImmovableOffset, 0);
                    host.Memory.Write<byte>(character + GroundedOffset, 0);
                    host.Memory.Write(character + AirborneTimerOffset, 0f);
                }
            }
            host.Log($"noclip {(_enabled ? "on" : "off")}");
            return 0;
        });

        host.AddLuaFunction("noclipKey", lua =>
        {
            lua.Push((long)_noclipKey);
            return 1;
        });

        host.AddLuaFunction("setNoclipKey", lua =>
        {
            int key = (int)lua.ToInteger(1);
            _noclipKey = key;
            host.Config.Set(KeyOption, key);
            host.Config.SaveDeferred();
            host.Log($"toggle key saved as 0x{key:X2}");
            lua.Push((long)key);
            return 1;
        });

        // Writes the character's position straight into memory. Lua refuses
        // client-side setWorldPosition, which forced every move through the
        // server and produced the one-tick correction we saw as jitter. A
        // memory write has no such restriction, so the client can place itself
        // at render rate with nothing to reconcile.
        // Counts how often each hook actually fires. If client_onUpdate is not
        // running at frame rate then no amount of correct positioning will look
        // smooth, because we simply are not moving the player often enough.


        host.AddLuaFunction("setPosition", lua =>
        {
            float x = (float)lua.ToNumber(1);
            float y = (float)lua.ToNumber(2);
            float z = (float)lua.ToNumber(3);

            // Write every copy we know about. The rendered one is the client's,
            // so writing only the server's leaves the visible character free to
            // predict against us.
            bool ok = PlaceCharacter(host, _clientCharacter, x, y, z, _renderOffset);
            ok |= PlaceCharacter(host, _character, x, y, z, _renderOffset);

            lua.Push(ok);
            return 1;
        });

        // Off by default: the engine's own flight mode moves the character now.
        // Set "teleportMovement": true in the config to fall back to the old
        // per-tick teleport, which works but visibly stutters.
        host.AddLuaFunction("teleportMovement", lua =>
        {
            lua.Push(host.Config.Get(TeleportOption, false));
            return 1;
        });

        host.AddLuaFunction("noclipSpeed", lua =>
        {
            lua.Push(_noclipSpeed);
            return 1;
        });

        host.AddLuaFunction("setNoclipSpeed", lua =>
        {
            double speed = Math.Clamp(lua.ToNumber(1), 0.05, 5.0);
            _noclipSpeed = speed;
            host.Config.Set(SpeedOption, speed);
            host.Config.SaveDeferred();
            host.Log($"speed saved as {speed}");
            lua.Push(speed);
            return 1;
        });

        // Movement follows the player's own bindings rather than hard-coded
        // WASD, so AZERTY (C_Forward = Z, C_Left = Q) works without config.
        host.AddLuaFunction("actionKey", lua =>
        {
            string action = lua.ToStringValue(1) ?? string.Empty;
            int fallback = (int)lua.ToInteger(2);
            lua.Push((long)host.GetKeyBinding(action, fallback));
            return 1;
        });

        // Resolves the engine Character behind the Lua userdata, by testing each
        // candidate pointer against the position the game itself reports. No
        // engine function is called, so there is no calling-convention risk.
        host.AddLuaFunction("bindCharacter", lua =>
        {
            nint userdata = LuaNative.lua_touserdata(lua.Handle, 1);
            var reported = new Vector3(
                (float)lua.ToNumber(2), (float)lua.ToNumber(3), (float)lua.ToNumber(4));

            // Client and server hold separate character objects - which is
            // precisely why Lua refuses client-side setWorldPosition. Writing
            // only the server's left the rendered copy free to predict, and
            // that mismatch is what kept showing up as jitter.
            bool isClient = lua.ToBoolean(5);
            nint found = 0;

            // Preferred: ask the engine's own resolver, then confirm the answer
            // against the position the game reported for this character.
            if (_resolveCharacter is not null)
            {
                nint slot = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteIntPtr(slot, 0);
                    _resolveCharacter(slot, lua.Handle, 1);
                    nint resolved = Marshal.ReadIntPtr(slot);

                    if (MatchesPosition(host, resolved, reported))
                    {
                        found = resolved;
                        host.Log($"{(isClient ? "client" : "server")} character " +
                                 $"0x{resolved:X} (engine resolver)");
                    }
                }
                catch (Exception ex)
                {
                    host.LogError("character resolver threw", ex);
                }
                finally
                {
                    Marshal.FreeHGlobal(slot);
                }
            }

            for (int offset = 0; found == 0 && offset <= 0x40 && userdata != 0; offset += 8)
            {
                nint candidate = host.Memory.Read<nint>(userdata + offset);
                if (candidate < 0x10000)
                    continue;

                // getWorldPosition reads [[character+0xB68]+0x10]+0x40, so a
                // candidate whose chain yields the reported position is the
                // Character we want.
                if (MatchesPosition(host, candidate, reported))
                {
                    found = candidate;
                    host.Log($"{(isClient ? "client" : "server")} character " +
                             $"0x{candidate:X} via userdata+0x{offset:X}");
                    break;
                }
            }

            if (isClient)
                _clientCharacter = found;
            else
                _character = found;

            if (found != 0)
            {
                nint body = host.Memory.ReadChain(found, PositionChainFirst, PositionChainSecond);
                if (body != 0)
                {
                    // All three components, deliberately. +0x1C0/+0x1C4 hold
                    // pointer-like values, so their offsets come out enormous
                    // and writing position+offset puts back very nearly the
                    // original bytes - which is what leaves them intact. Zeroing
                    // those offsets writes raw coordinates over the pointers and
                    // freezes the camera.
                    _renderOffset = new Vector3(
                        host.Memory.Read<float>(body + RenderMirror + 0) - host.Memory.Read<float>(body + PositionOffset + 0),
                        host.Memory.Read<float>(body + RenderMirror + 4) - host.Memory.Read<float>(body + PositionOffset + 4),
                        host.Memory.Read<float>(body + RenderMirror + 8) - host.Memory.Read<float>(body + PositionOffset + 8));

                    host.Log($"render offset ({_renderOffset.X:E2}, {_renderOffset.Y:E2}, {_renderOffset.Z:F3})");
                }
            }

            if (found == 0)
                host.LogError($"could not resolve the {(isClient ? "client" : "server")} character");

            lua.Push(found != 0);
            return 1;
        });

        // The actual fix: velocity lives at character+0xA0 as three floats, and
        // nothing in the Lua API can clear it. Zeroing it each tick stops the
        // teleports from accumulating speed - which is what built up the jitter
        // and fired the player through the world on release.
        // Field discovery, done by measurement rather than by reading offsets
        // out of a disassembly - which is how I got +0xA0 wrong. Snapshots the
        // character object each second and reports the floats that actually
        // move, so velocity identifies itself.

        PatchCharacterGuards(host);

        host.PatchScript("CreativePlayer.lua", context => context.Append(PlayerPatch));
        host.PatchScript("CreativeGame.lua", context => context.Append(GamePatch));
        host.Log("ready");
    }

    /// <summary>
    /// Appended to CreativePlayer.lua. Only touches globals the sandbox
    /// provides - calling a stripped global would abort the chunk and make the
    /// engine fail to load the script entirely.
    /// </summary>
    private const string PlayerPatch = """
        -- Position is owned by the server. An earlier version teleported from
        -- the client every tick, which raced the character's own physics: the
        -- fall velocity kept accumulating between corrections, so the
        -- correction grew and the jitter got worse the longer noclip was on.
        --
        -- Here the server holds an anchor and the client only sends a direction,
        -- and gravity is switched off entirely for the duration - so there is no
        -- velocity to accumulate in the first place.
        -- The engine compiles this file once per lua_State, and a world load
        -- creates about ten of them. Without this guard each compile wrapped
        -- the previous wrapper, so a single tick ran the movement code once per
        -- compile - advancing the anchor many times per tick. That, not any
        -- velocity field, was the jitter.
        if not CreativePlayer.smloaderPatched then
        CreativePlayer.smloaderPatched = true

        function CreativePlayer.sv_smloader_noclip( self, params )
            local character = self.player:getCharacter()
            if not character then
                return
            end

            if params.enabled == false then
                self.sv.smloaderNoclip = false
                self.sv.smloaderSettleTicks = nil
                self.sv.smloaderHoldPos = nil

                -- Commit the exit position through the engine's own path. While
                -- noclip is on the body is pinned immovable and moved by direct
                -- memory writes, so the engine's authoritative position never
                -- changed - without this it reconciles you back to wherever you
                -- switched noclip on.
                if params.pos then
                    character:setWorldPosition( params.pos )
                end
                return
            end

            if params.enabled ~= nil then
                self.sv.smloaderNoclip = params.enabled
                self.sv.smloaderSettleTicks = nil
                self.sv.smloaderHoldPos = nil

                -- setFlying and setHovering are both refused for player
                -- characters. The closure matters: pcall( character.setX, ... )
                -- would index the userdata while evaluating arguments, so a
                -- missing member raises before pcall can catch it.
                -- The engine's own flight mode. Normally refused for player
                -- characters, but SMLoader NOPs that check natively at startup,
                -- so gravity stops acting on the body and there is no velocity
                -- for our teleports to fight.
                if not pcall( function() character:setFlying( params.enabled ) end ) then
                    sm.log.info( "[SMLoader] setFlying still refused - native patch missing?" )
                end

                -- Let the engine hold the body still rather than us re-writing
                -- the same position every frame. That write was happening after
                -- the frame's transform had already been taken, so each frame
                -- drew the engine's position and then got corrected - which is
                -- the vibration seen even while standing completely still.
                pcall( function() character:setImmovable( params.enabled ) end )

                if params.enabled and smloader.bindCharacter then
                    local p = character:getWorldPosition()
                    smloader.bindCharacter( character, p.x, p.y, p.z, false )
                end

                -- Deliberately NOT touching sm.physics.setGravity: it changes
                -- gravity for the whole world, not just the player, so it also
                -- affects every creation - and if getGravity ever failed, the
                -- original value could never be restored and the world would
                -- stay weightless for the rest of the session.
            end

            if params.pos then
                character:setWorldPosition( params.pos )

                -- Clear the velocity the engine derives from that teleport,
                -- before it can accumulate into the next tick.
                if smloader.zeroVelocity then
                    smloader.zeroVelocity()
                end
            end
        end

        -- Runs the settle-down after noclip is switched off: the body is pinned
        -- in place for a few ticks so the velocity implied by our teleports
        -- decays, and only then are immovable/climbing released.
        local smloader_originalServerFixedUpdate = CreativePlayer.server_onFixedUpdate

        CreativePlayer.server_onFixedUpdate = function( self, ... )
            if self.sv and self.sv.smloaderSettleTicks then
                local character = self.player:getCharacter()

                if character and self.sv.smloaderHoldPos then
                    character:setWorldPosition( self.sv.smloaderHoldPos )
                end

                -- Keep it at zero through the whole settle, so control is
                -- handed back with no speed left over.
                if smloader.zeroVelocity then
                    smloader.zeroVelocity()
                end

                self.sv.smloaderSettleTicks = self.sv.smloaderSettleTicks - 1

                if self.sv.smloaderSettleTicks <= 0 then
                    if character then
                        pcall( function() character:setImmovable( false ) end )
                        pcall( function() character:setClimbing( false ) end )
                    end
                    self.sv.smloaderSettleTicks = nil
                    self.sv.smloaderHoldPos = nil
                end
            end

            if smloader_originalServerFixedUpdate then
                return smloader_originalServerFixedUpdate( self, ... )
            end
        end

        local smloader_originalFixedUpdate = CreativePlayer.client_onFixedUpdate

        CreativePlayer.client_onFixedUpdate = function( self, ... )
            if smloader and smloader.isKeyDown then
                local down = smloader.isKeyDown( smloader.noclipKey() )
                if down and not SMLOADER_KEY_DOWN then
                    -- Refuse while seated. A seat drives the character's own
                    -- position every tick, so pinning the position mirrors
                    -- fights it directly - the camera ends up split between the
                    -- seat and the noclip anchor.
                    local seated = false
                    local seatedCharacter = self.player:getCharacter()
                    if seatedCharacter then
                        local ok, result = pcall( function()
                            return seatedCharacter:isSeated()
                        end )
                        seated = ok and result
                    end

                    if seated and not smloader.isNoclip() then
                        self.cl.smloaderToast = "NOCLIP UNAVAILABLE WHILE SEATED"
                        self.cl.smloaderToastLeft = 2.0
                        SMLOADER_KEY_DOWN = down
                        return smloader_originalFixedUpdate
                            and smloader_originalFixedUpdate( self, ... )
                    end

                    local enabled = not smloader.isNoclip()
                    smloader.setNoclip( enabled )

                    -- Commit the flown-to position on the way out. Without this
                    -- the server simply stops being told where we are and
                    -- reconciles the character back to wherever it last
                    -- simulated it, undoing the whole flight.
                    self.network:sendToServer( "sv_smloader_noclip",
                        { enabled = enabled, pos = self.cl.smloaderAnchor } )

                    self.cl.smloaderAnchor = nil

                    -- The camera is deliberately left attached. Detaching it
                    -- into cutsceneFP did hide the body's jitter, but that state
                    -- also cuts the player's look input - getDirection stopped
                    -- tracking the mouse, so the view froze and the FOV changed.
                    -- Losing mouse look is a far worse trade than the jitter.

                    -- Vanilla bottom-centre prompt rather than the chat log.
                    -- It has to be re-issued every frame, so it is driven from
                    -- client_onUpdate for a couple of seconds.
                    self.cl.smloaderToast = enabled and "NOCLIP ON" or "NOCLIP OFF"
                    self.cl.smloaderToastLeft = 2.0
                end
                SMLOADER_KEY_DOWN = down


                if smloader.isNoclip() then
                    -- Read once per session: these come from the player's own
                    -- keybinds.json, so the layout follows whatever they play on.
                    if not self.cl.smloaderKeys then
                        self.cl.smloaderKeys = {
                            forward  = smloader.actionKey( "C_Forward",  87 ),
                            backward = smloader.actionKey( "C_Backward", 83 ),
                            left     = smloader.actionKey( "C_Left",     65 ),
                            right    = smloader.actionKey( "C_Right",    68 ),
                            up       = smloader.actionKey( "C_Jump",     32 ),
                            down     = smloader.actionKey( "Crawl",      17 ),
                        }
                    end

                    -- Movement itself happens in client_onUpdate, which runs
                    -- per rendered frame. Doing it here, at the 40 Hz fixed
                    -- tick, meant the position stepped far more coarsely than
                    -- the game draws - visible as judder however correct the
                    -- values were.
                    local character = self.player:getCharacter()
                    if character then
                        if not self.cl.smloaderAnchor then
                            self.cl.smloaderAnchor = character:getWorldPosition()

                            local p = self.cl.smloaderAnchor
                            smloader.bindCharacter( character, p.x, p.y, p.z, true )
                        end

                        -- Deliberately NOT syncing the position to the server
                        -- each tick any more. The server applied it with
                        -- setWorldPosition at 40 Hz using a value one tick old,
                        -- which fought the per-frame writes that now own the
                        -- position outright. The body is pinned immovable, so
                        -- there is nothing for the server to correct.
                    end
                end
            end

            if smloader_originalFixedUpdate then
                return smloader_originalFixedUpdate( self, ... )
            end
        end

        local smloader_originalClientUpdate = CreativePlayer.client_onUpdate

        CreativePlayer.client_onUpdate = function( self, dt, ... )
            if self.cl.smloaderToastLeft and self.cl.smloaderToastLeft > 0 then
                self.cl.smloaderToastLeft = self.cl.smloaderToastLeft - ( dt or 0.016 )
                sm.gui.setInteractionText( "", "", self.cl.smloaderToast )
            end


            if smloader and smloader.isNoclip() and self.cl.smloaderAnchor
               and smloader.setPosition then
                local keys = self.cl.smloaderKeys
                local character = self.player:getCharacter()
                if keys and character then
                    local move = sm.vec3.new( 0, 0, 0 )
                    local moving = false
                    local direction = sm.localPlayer.getDirection()
                    local right = sm.localPlayer.getRight()
                    local up = sm.vec3.new( 0, 0, 1 )

                    if smloader.isKeyDown( keys.forward )  then move = move + direction; moving = true end
                    if smloader.isKeyDown( keys.backward ) then move = move - direction; moving = true end
                    if smloader.isKeyDown( keys.right )    then move = move + right;     moving = true end
                    if smloader.isKeyDown( keys.left )     then move = move - right;     moving = true end
                    if smloader.isKeyDown( keys.up )       then move = move + up;        moving = true end
                    if smloader.isKeyDown( keys.down )     then move = move - up;        moving = true end

                    if moving and move:length() > 0 then
                        -- Speed is configured per 40 Hz tick, so scale it by the
                        -- frame time to keep the same feel at any frame rate.
                        local perSecond = smloader.noclipSpeed() * 40
                        self.cl.smloaderAnchor = self.cl.smloaderAnchor
                            + move:normalize() * ( perSecond * ( dt or 0.016 ) )

                    end

                    -- Written every frame, moving or not: leaving any copy
                    -- unpinned is what lets them drift apart.
                    local a = self.cl.smloaderAnchor
                    smloader.setPosition( a.x, a.y, a.z )
                end
            end

            if smloader_originalClientUpdate then
                return smloader_originalClientUpdate( self, dt, ... )
            end
        end

        end -- CreativePlayer.smloaderPatched
        """;

    /// <summary>
    /// Appended to CreativeGame.lua, which is where the game registers its own
    /// chat commands - so these sit alongside /weather and /timeofday.
    /// </summary>
    private const string GamePatch = """
        if not CreativeGame.smloaderPatched then
        CreativeGame.smloaderPatched = true

        function CreativeGame.cl_smloader_command( self, params )
            local command = params[1]

            if command == "/noclip" then
                local enabled = not smloader.isNoclip()
                smloader.setNoclip( enabled )
                sm.gui.chatMessage( "noclip " .. ( enabled and "ON" or "OFF" ) )

            elseif command == "/noclipkey" then
                local key = params[2]
                if key and key > 0 then
                    smloader.setNoclipKey( key )
                    sm.gui.chatMessage( "noclip toggle key set to virtual-key " .. tostring( key ) )
                end

            elseif command == "/noclipspeed" then
                local speed = params[2]
                if speed then
                    sm.gui.chatMessage( "noclip speed set to "
                        .. tostring( smloader.setNoclipSpeed( speed ) ) )
                end

            elseif command == "/smloader" then
                sm.gui.chatMessage( "SMLoader: noclip is "
                    .. ( smloader.isNoclip() and "ON" or "OFF" )
                    .. ", toggle key " .. tostring( smloader.noclipKey() )
                    .. ", speed " .. tostring( smloader.noclipSpeed() ) )
                sm.gui.chatMessage( "Commands: /noclip  /noclipkey <vk>  /noclipspeed <n>  /smloader" )
            end
        end

        local smloader_originalClientOnCreate = CreativeGame.client_onCreate

        local smloader_clientOnCreate = function( self, ... )
            local result
            if smloader_originalClientOnCreate then
                result = smloader_originalClientOnCreate( self, ... )
            end

            sm.game.bindChatCommand( "/noclip", {}, "cl_smloader_command",
                "Toggle SMLoader noclip" )
            sm.game.bindChatCommand( "/noclipkey", { { "number", "virtualKey", false } },
                "cl_smloader_command", "Set the noclip toggle key (113 = F2)" )
            sm.game.bindChatCommand( "/noclipspeed", { { "number", "metresPerTick", false } },
                "cl_smloader_command", "Set the noclip movement speed (default 0.4)" )
            sm.game.bindChatCommand( "/smloader", {}, "cl_smloader_command",
                "Show SMLoader status and commands" )

            return result
        end

        -- This file ends by deriving ClassicCreativeGame / CreativeCustomGame /
        -- CreativeTerrainGame from CreativeGame, and a real world runs one of
        -- those. Because our code is appended after those class() calls, they
        -- already exist, so assigning only to the base class would never be
        -- reached - which is exactly why the commands did not register.
        CreativeGame.client_onCreate = smloader_clientOnCreate

        for _, derived in pairs( { ClassicCreativeGame, CreativeCustomGame, CreativeTerrainGame } ) do
            derived.cl_smloader_command = CreativeGame.cl_smloader_command
            derived.client_onCreate = smloader_clientOnCreate
        end

        end -- CreativeGame.smloaderPatched
        """;

    /// <summary>
    /// The engine's own "userdata at this Lua index -> Character*" helper.
    /// x64 has a single calling convention, so Cdecl marshals correctly.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResolveCharacterDelegate(nint result, nint luaState, int index);

    private ResolveCharacterDelegate? _resolveCharacter;

    /// <summary>
    /// Picks up the resolver the setFlying binding itself uses. It sits 0x1B
    /// bytes before the guard we already located:
    ///     lea rcx,[rsp+0x50] ; mov rdx,rbx ; mov r8d,1 ; call &lt;resolver&gt;
    /// Deriving it from the same signature keeps the two in step.
    /// </summary>
    private void CaptureCharacterResolver(IModHost host, nint guardSite)
    {
        nint call = guardSite - 0x1B;

        if (host.Memory.Read<byte>(call) != 0xE8)
        {
            host.LogError($"expected a call at 0x{call:X}; character resolver unavailable");
            return;
        }

        nint resolver = call + 5 + host.Memory.Read<int>(call + 1);
        _resolveCharacter = Marshal.GetDelegateForFunctionPointer<ResolveCharacterDelegate>(resolver);
        host.Log($"character resolver at 0x{resolver:X}");
    }

    /// <summary>
    /// Lifts the engine's "Player characters can not activate flying/hovering"
    /// restriction so the game's own flight mode can be used on the player.
    ///
    /// The Lua binding for each does:
    ///     cmp qword [rbx+0x38], 0    ; is this character owned by a player?
    ///     jnz  &lt;error path&gt;          ; if so, refuse
    ///     cmp  al, [rbx+0x63]        ; otherwise set the flag
    /// Replacing that jnz with two NOPs removes the check and nothing else.
    ///
    /// This is tied to one build of the game: the signature is verified before
    /// writing, and a miss is reported rather than patched blindly.
    /// </summary>
    private void PatchCharacterGuards(IModHost host)
    {
        // Identical guards; the trailing byte is the flag offset each one sets.
        PatchGuard(host, "setFlying", "48 83 7B 38 00 75 1D 3A 43 63");
        PatchGuard(host, "setHovering", "48 83 7B 38 00 75 1D 3A 43 64");
    }

    private void PatchGuard(IModHost host, string name, string pattern)
    {
        nint site = host.Memory.FindPattern(pattern);
        if (site == 0)
        {
            host.LogError($"{name}: guard signature not found - the game has probably " +
                          "updated, so the native patch is skipped");
            return;
        }

        if (name == "setFlying")
            CaptureCharacterResolver(host, site);

        nint jump = site + 5;
        byte[] existing = host.Memory.ReadBytes(jump, 2);

        if (existing.Length != 2 || existing[0] != 0x75)
        {
            host.LogError($"{name}: expected a jnz at 0x{jump:X}, found " +
                          $"{Convert.ToHexString(existing)} - not patching");
            return;
        }

        if (!host.Memory.TryUnprotect(jump, 2, out uint previous))
        {
            host.LogError($"{name}: could not make 0x{jump:X} writable - not patching");
            return;
        }

        bool written;
        try
        {
            written = host.Memory.WriteBytes(jump, new byte[] { 0x90, 0x90 });
        }
        finally
        {
            // Restoring in a finally is what keeps a page of the game's .text
            // from staying writable for the rest of the session.
            if (!host.Memory.Protect(jump, 2, previous))
                host.LogError($"{name}: page left writable at 0x{jump:X}");
        }

        host.Log(written
            ? $"{name}: player restriction lifted at 0x{jump:X} (jnz -> nop nop)"
            : $"{name}: write to 0x{jump:X} failed");
    }

    /// <summary>
    /// Breadth-first walk of the pointers reachable from the character handle,
    /// scanning each block for the known world position. Bounded in both depth
    /// and node count so a bad hop cannot turn into a long stall on the game
    /// thread.
    /// </summary>
    private static void ExplorePointerGraph(IModHost host, nint root, Vector3 position)
    {
        const int maxDepth = 3;
        const int maxNodes = 160;
        const int blockSize = 0x800;

        var seen = new HashSet<nint> { root };
        var queue = new Queue<(nint Address, string Label, int Depth)>();
        queue.Enqueue((root, "handle", 0));

        int visited = 0;

        while (queue.Count > 0 && visited < maxNodes)
        {
            (nint address, string label, int depth) = queue.Dequeue();
            visited++;

            SearchForPosition(host, address, blockSize, position, label);

            if (depth >= maxDepth)
                continue;

            // Only the head of each block is treated as pointer slots; scanning
            // the whole block as pointers explodes the search for no benefit.
            for (int offset = 0; offset < 0x80; offset += 8)
            {
                nint next = host.Memory.Read<nint>(address + offset);

                // User-space heap pointers only - this rejects small integers,
                // packed handles and kernel addresses.
                if (next < 0x10000 || next > 0x7FFFFFFFFFFF)
                    continue;
                if (!seen.Add(next) || !host.Memory.IsReadable(next, 0x40))
                    continue;

                queue.Enqueue((next, $"{label}+0x{offset:X}", depth + 1));
            }
        }

        host.Log($"pointer walk visited {visited} block(s)");
    }

    /// <summary>
    /// Looks for three consecutive floats (and doubles) matching a known world
    /// position. Whatever region contains them is the engine's character state,
    /// and the offsets found here are what the native noclip will write to.
    /// </summary>
    private static void SearchForPosition(IModHost host, nint start, int size,
                                          Vector3 position, string label)
    {
        byte[] data = host.Memory.ReadBytes(start, size);
        if (data.Length == 0)
            return;

        int hits = 0;

        for (int offset = 0; offset + 12 <= data.Length && hits < 8; offset += 4)
        {
            if (Near(BitConverter.ToSingle(data, offset), position.X) &&
                Near(BitConverter.ToSingle(data, offset + 4), position.Y) &&
                Near(BitConverter.ToSingle(data, offset + 8), position.Z))
            {
                hits++;
                host.Log($"  {label}: float vec3 at +0x{offset:X} (0x{start + offset:X})");
                host.Log("  surrounding:\n" + host.Memory.HexDump(
                    start + Math.Max(0, offset - 32), 96));
            }
        }

        for (int offset = 0; offset + 24 <= data.Length && hits < 12; offset += 4)
        {
            if (Near(BitConverter.ToDouble(data, offset), position.X) &&
                Near(BitConverter.ToDouble(data, offset + 8), position.Y) &&
                Near(BitConverter.ToDouble(data, offset + 16), position.Z))
            {
                hits++;
                host.Log($"  {label}: double vec3 at +0x{offset:X} (0x{start + offset:X})");
            }
        }

        // Silent on a miss: the walk visits well over a hundred blocks and only
        // the hits are interesting.
    }

    /// <summary>
    /// Confirms a candidate really is the Character, by walking the same chain
    /// getWorldPosition uses and comparing against the position the game just
    /// reported. Guessing wrong here would mean writing into an unrelated
    /// object, so nothing is trusted without this check.
    /// </summary>
    private static bool MatchesPosition(IModHost host, nint candidate, Vector3 reported)
    {
        if (candidate < 0x10000)
            return false;

        nint body = host.Memory.ReadChain(candidate, PositionChainFirst, PositionChainSecond);
        if (body == 0)
            return false;

        return Near(host.Memory.Read<float>(body + PositionOffset + 0), reported.X)
            && Near(host.Memory.Read<float>(body + PositionOffset + 4), reported.Y)
            && Near(host.Memory.Read<float>(body + PositionOffset + 8), reported.Z);
    }

    /// <summary>
    /// Diffs the character object against the previous second and lists the
    /// floats that changed most. A velocity field shows up as three adjacent
    /// entries that grow while falling and sit near zero at rest.
    /// </summary>
    /// <summary>
    /// Pins a character at a position and clears the state the engine would
    /// otherwise use to derive a fall from that movement.
    /// </summary>
    private static bool PlaceCharacter(IModHost host, nint character, float x, float y, float z,
                                       Vector3 renderOffset)
    {
        if (character == 0)
            return false;

        bool ok = false;

        foreach (int mirror in CharacterMirrors)
        {
            ok |= host.Memory.Write(character + mirror + 0, x);
            host.Memory.Write(character + mirror + 4, y);
            host.Memory.Write(character + mirror + 8, z);
        }

        nint body = host.Memory.ReadChain(character, PositionChainFirst, PositionChainSecond);
        if (body != 0)
        {
            foreach (int mirror in BodyMirrors)
            {
                ok |= host.Memory.Write(body + mirror + 0, x);
                host.Memory.Write(body + mirror + 4, y);
                host.Memory.Write(body + mirror + 8, z);
            }
        }

        if (body != 0)
        {
            host.Memory.Write(body + RenderMirror + 0, x + renderOffset.X);
            host.Memory.Write(body + RenderMirror + 4, y + renderOffset.Y);
            host.Memory.Write(body + RenderMirror + 8, z + renderOffset.Z);
        }

        host.Memory.Write<byte>(character + GroundedOffset, 1);
        host.Memory.Write(character + AirborneTimerOffset, 0f);
        host.Memory.Write<byte>(character + ImmovableOffset, 1);
        host.Memory.Write<byte>(character + TumblingOffset, 0);
        return ok;
    }

    private static bool Near(double value, double target) => Math.Abs(value - target) < 0.05;

    /// <summary>
    /// True only while the game window has focus. Keyboard state is read
    /// process-wide, so every key check has to be gated on this.
    /// </summary>
    private static uint _focusStamp;
    private static bool _focused;

    /// <summary>
    /// Whether the game owns the foreground window, recomputed at most every
    /// 100ms. isKeyDown asks per key per frame - six movement keys plus the
    /// toggle - so this was ~14 window-manager round trips a frame for a value
    /// that changes when the player alt-tabs.
    /// </summary>
    private static bool IsGameFocused()
    {
        uint now = (uint)Environment.TickCount;
        if (now - _focusStamp < 100)
            return _focused;

        _focusStamp = now;

        nint foreground = GetForegroundWindow();
        if (foreground == 0)
            return _focused = false;

        GetWindowThreadProcessId(foreground, out uint processId);
        return _focused = processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
