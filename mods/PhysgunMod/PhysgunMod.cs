using System.Runtime.InteropServices;
using SMLoader.Api;
using SMLoader.Api.Lua;

namespace PhysgunMod;

/// <summary>
/// A Garry's Mod style physics gun for creative mode. No game file is modified:
/// CreativePlayer.lua and CreativeGame.lua are rewritten in memory as the engine
/// compiles them.
/// </summary>
/// <remarks>
/// The split follows NoclipMod's, for the same two reasons: the game's fixed
/// action bindings cannot express an arbitrary hotkey, and each game script gets
/// its own environment, so a Lua global cannot carry state between scripts.
/// <para>
/// Everything physical lives in Lua because that is where <c>sm.physics</c> is,
/// and it lives on the <em>server</em> half of the player script because the
/// server owns body positions - the same lesson NoclipMod's PlayerPatch records
/// about the character. The client only ever sends where it is aiming.
/// </para>
/// <para>
/// Nothing here moves a body directly. Scrap Mechanic binds no
/// <c>setWorldPosition</c> or <c>setVelocity</c> on a Body - checked against the
/// game's own scripts, where those methods only ever appear on characters,
/// triggers and effects - so the hold is a damped spring driven by
/// <c>sm.physics.applyImpulse</c>. That is a constraint, but it is also what
/// makes the thing feel like a physgun rather than like a teleport.
/// </para>
/// </remarks>
public sealed class PhysgunMod : IMod
{
    private const string ToggleKeyOption = "toggleKey";
    private const string GrabKeyOption = "grabKey";
    private const string FreezeKeyOption = "freezeKey";
    private const string UnfreezeKeyOption = "unfreezeKey";
    private const string PushKeyOption = "pushKey";
    private const string PullKeyOption = "pullKey";
    private const string RangeOption = "range";
    private const string StiffnessOption = "stiffness";
    private const string DampingOption = "damping";
    private const string AngularDampingOption = "angularDamping";
    private const string MaxMassOption = "maxMass";
    private const string GravityOption = "gravity";
    private const string BeamOption = "beam";
    private const string DebugOption = "debug";

    private const int DefaultToggleKey = 0x47;   // VK_G
    private const int DefaultGrabKey = 0x01;     // VK_LBUTTON
    private const int DefaultFreezeKey = 0x02;   // VK_RBUTTON
    private const int DefaultUnfreezeKey = 0x52; // VK_R
    private const int DefaultPushKey = 0x21;     // VK_PRIOR (PageUp)
    private const int DefaultPullKey = 0x22;     // VK_NEXT  (PageDown)

    // Stiffness is in 1/s^2 and the damping terms in 1/s: the hold applies
    // mass * k * error * dt as an impulse, so k is an acceleration per metre of
    // error. Critical damping for k = 12 is 2*sqrt(12) = 6.93, so 6.0 lands just
    // inside it - settled, with enough spring left to read as a physgun.
    private const double DefaultRange = 40.0;
    private const double DefaultStiffness = 12.0;
    private const double DefaultDamping = 6.0;
    private const double DefaultAngularDamping = 6.0;
    private const double DefaultMaxMass = 0.0;   // 0 = no limit
    private const double DefaultGravity = 9.81;  // cancelled per tick, so held bodies hang

    public string Name => "PhysgunMod";

    public string Version => "1.0.0";

    // Every one of these is read from Lua at 40 Hz, so they are cached here and
    // kept current from IModSettings.Changed rather than re-read - the same
    // reason NoclipMod caches its two. Config.Get is a lock plus a full
    // System.Text.Json converter dispatch.
    private int _toggleKey = DefaultToggleKey;
    private int _grabKey = DefaultGrabKey;
    private int _freezeKey = DefaultFreezeKey;
    private int _unfreezeKey = DefaultUnfreezeKey;
    private int _pushKey = DefaultPushKey;
    private int _pullKey = DefaultPullKey;
    private double _range = DefaultRange;
    private double _stiffness = DefaultStiffness;
    private double _damping = DefaultDamping;
    private double _angularDamping = DefaultAngularDamping;
    private double _maxMass = DefaultMaxMass;
    private double _gravity = DefaultGravity;
    private bool _beam = true;
    private bool _debug;

    /// <summary>
    /// Physgun mode. Off by default and toggled explicitly, because the grab key
    /// defaults to left mouse and creative mode already has a use for that.
    /// </summary>
    private bool _enabled;

    /// <summary>
    /// Held-key bitmask from the previous poll, which is what turns a level into
    /// the press and release edges Lua actually wants. One poll per fixed tick,
    /// from the local player's script only, so there is exactly one reader.
    /// </summary>
    private int _previous;

    /// <summary>Set by <c>/unfreezeall</c>, consumed by the next poll.</summary>
    private bool _unfreezeAllPending;

    private const int MaskToggle = 1 << 0;
    private const int MaskGrab = 1 << 1;
    private const int MaskFreeze = 1 << 2;
    private const int MaskUnfreeze = 1 << 3;
    private const int MaskPush = 1 << 4;
    private const int MaskPull = 1 << 5;

    public void OnLoad(IModHost host)
    {
        // Declared rather than just stored: SMLoader persists these and shows
        // them in the shared settings panel, so this mod needs no UI of its own.
        DeclareKey(host, ToggleKeyOption, "Physgun mode", DefaultToggleKey,
                   "Turns the physgun on and off");
        DeclareKey(host, GrabKeyOption, "Physgun grab", DefaultGrabKey,
                   "Hold to carry what you are looking at (default left mouse)");
        DeclareKey(host, FreezeKeyOption, "Physgun freeze", DefaultFreezeKey,
                   "While carrying, pins the object where it is (default right mouse)");
        DeclareKey(host, UnfreezeKeyOption, "Physgun unfreeze", DefaultUnfreezeKey,
                   "Releases the frozen object you are looking at");
        DeclareKey(host, PushKeyOption, "Physgun push", DefaultPushKey,
                   "Pushes the carried object further away");
        DeclareKey(host, PullKeyOption, "Physgun pull", DefaultPullKey,
                   "Pulls the carried object closer");

        DeclareNumber(host, RangeOption, "Physgun range", DefaultRange,
                      "How far the beam reaches, in metres", 5.0, 250.0, 5.0);
        DeclareNumber(host, StiffnessOption, "Physgun strength", DefaultStiffness,
                      "How hard the beam pulls towards the aim point", 2.0, 40.0, 1.0);
        DeclareNumber(host, DampingOption, "Physgun damping", DefaultDamping,
                      "Higher settles faster; below 2*sqrt(strength) it springs", 0.0, 30.0, 0.5);
        DeclareNumber(host, AngularDampingOption, "Physgun spin damping", DefaultAngularDamping,
                      "How quickly a carried object stops tumbling", 0.0, 30.0, 0.5);
        DeclareNumber(host, MaxMassOption, "Physgun mass limit", DefaultMaxMass,
                      "Refuse to grab anything heavier; 0 lifts anything", 0.0, 100000.0, 100.0);
        DeclareNumber(host, GravityOption, "Physgun lift", DefaultGravity,
                      "Gravity cancelled while carrying; lower it to make things sag", 0.0, 30.0, 0.5);

        host.Settings.Declare<bool>(new ModSetting(
            BeamOption, "Physgun beam", SettingKind.Toggle, true)
        {
            Description = "Draw the beam between the hand and the carried object",
        });

        host.Settings.Declare<bool>(new ModSetting(
            DebugOption, "Physgun diagnostics", SettingKind.Toggle, false)
        {
            Description = "Log the aim, target and hold error to smloader.log once a second",
        });

        ReadSettings(host);
        host.Settings.Changed += _ => ReadSettings(host);

        host.Log($"mode key 0x{_toggleKey:X2}, grab 0x{_grabKey:X2}, range {_range} m");

        // One crossing per fixed tick instead of six. Edge detection lives here
        // rather than in Lua so the script does not have to carry was-down
        // globals across an environment it does not own.
        host.AddLuaFunction("physgunPoll", lua =>
        {
            // GetAsyncKeyState is global to the machine. Without the focus gate
            // the physgun would keep grabbing while the game is alt-tabbed.
            bool focused = IsGameFocused();

            int held = 0;
            if (focused)
            {
                if (IsDown(_toggleKey)) held |= MaskToggle;
                if (IsDown(_grabKey)) held |= MaskGrab;
                if (IsDown(_freezeKey)) held |= MaskFreeze;
                if (IsDown(_unfreezeKey)) held |= MaskUnfreeze;
                if (IsDown(_pushKey)) held |= MaskPush;
                if (IsDown(_pullKey)) held |= MaskPull;
            }

            int pressed = held & ~_previous;
            int released = ~held & _previous;
            _previous = held;

            bool toggled = (pressed & MaskToggle) != 0;
            if (toggled)
                _enabled = !_enabled;

            bool unfreezeAll = _unfreezeAllPending;
            _unfreezeAllPending = false;

            if (!lua.EnsureStack(3))
                return 0;

            lua.NewTable();
            PushField(lua, "enabled", _enabled);
            PushField(lua, "toggled", toggled);
            PushField(lua, "grab", (held & MaskGrab) != 0);
            PushField(lua, "grabDown", (pressed & MaskGrab) != 0);
            PushField(lua, "grabUp", (released & MaskGrab) != 0);
            PushField(lua, "freezeDown", (pressed & MaskFreeze) != 0);
            PushField(lua, "unfreezeDown", (pressed & MaskUnfreeze) != 0);
            PushField(lua, "push", (held & MaskPush) != 0);
            PushField(lua, "pull", (held & MaskPull) != 0);
            PushField(lua, "unfreezeAll", unfreezeAll);
            return 1;
        });

        // Read by both halves of the player script - the client for the range of
        // its raycast, the server for the spring - and polled rather than pushed,
        // because a mod has no way to reach into a live lua_State.
        host.AddLuaFunction("physgunTuning", lua =>
        {
            if (!lua.EnsureStack(3))
                return 0;

            lua.NewTable();
            PushField(lua, "range", _range);
            PushField(lua, "stiffness", _stiffness);
            PushField(lua, "damping", _damping);
            PushField(lua, "angularDamping", _angularDamping);
            PushField(lua, "maxMass", _maxMass);
            PushField(lua, "gravity", _gravity);
            PushField(lua, "beam", _beam);
            PushField(lua, "debug", _debug);
            return 1;
        });

        host.AddLuaFunction("physgunEnabled", lua =>
        {
            lua.Push(_enabled);
            return 1;
        });

        host.AddLuaFunction("physgunSetEnabled", lua =>
        {
            _enabled = lua.ToBoolean(1);
            lua.Push(_enabled);
            return 1;
        });

        host.AddLuaFunction("physgunSetRange", lua =>
        {
            _range = Store(host, RangeOption, Math.Clamp(lua.ToNumber(1), 5.0, 250.0));
            lua.Push(_range);
            return 1;
        });

        host.AddLuaFunction("physgunSetStiffness", lua =>
        {
            _stiffness = Store(host, StiffnessOption, Math.Clamp(lua.ToNumber(1), 2.0, 40.0));
            lua.Push(_stiffness);
            return 1;
        });

        host.AddLuaFunction("physgunSetDebug", lua =>
        {
            _debug = lua.ToBoolean(1);
            host.Config.Set(DebugOption, _debug);
            host.Config.SaveDeferred();
            lua.Push(_debug);
            return 1;
        });

        host.AddLuaFunction("physgunDebug", lua =>
        {
            lua.Push(_debug);
            return 1;
        });

        host.AddLuaFunction("physgunUnfreezeAll", lua =>
        {
            // Only a request: the frozen list lives on the server half of the
            // player script, and this runs on whichever state called it.
            _unfreezeAllPending = true;
            lua.Push(true);
            return 1;
        });

        // The player script wraps three engine callbacks in a pcall. Without
        // somewhere to report, a failure in there would be silent - and a silent
        // failure in server_onFixedUpdate looks exactly like "the physgun does
        // nothing", which is the least diagnosable bug this mod could have.
        host.AddLuaFunction("physgunLog", lua =>
        {
            host.LogError(lua.ToStringValue(1) ?? "(no message)");
            return 0;
        });

        host.PatchScript("CreativePlayer.lua", context => context.Append(PlayerPatch));
        host.PatchScript("CreativeGame.lua", context => context.Append(GamePatch));
        host.Log("ready");
    }

    private static void DeclareKey(IModHost host, string key, string label, int fallback,
                                   string description)
    {
        host.Settings.Declare<int>(new ModSetting(key, label, SettingKind.Key, fallback)
        {
            Description = description,
        });
    }

    private static void DeclareNumber(IModHost host, string key, string label, double fallback,
                                      string description, double minimum, double maximum,
                                      double step)
    {
        host.Settings.Declare<double>(new ModSetting(key, label, SettingKind.Number, fallback)
        {
            Description = description,
            Minimum = minimum,
            Maximum = maximum,
            Step = step,
        });
    }

    /// <summary>
    /// Refreshes every cached setting. Called for any key rather than switching
    /// on which one changed: there are thirteen of them, this runs when a player
    /// moves a slider, and a switch is one more place for a new setting to be
    /// forgotten.
    /// </summary>
    private void ReadSettings(IModHost host)
    {
        _toggleKey = host.Config.Get(ToggleKeyOption, DefaultToggleKey);
        _grabKey = host.Config.Get(GrabKeyOption, DefaultGrabKey);
        _freezeKey = host.Config.Get(FreezeKeyOption, DefaultFreezeKey);
        _unfreezeKey = host.Config.Get(UnfreezeKeyOption, DefaultUnfreezeKey);
        _pushKey = host.Config.Get(PushKeyOption, DefaultPushKey);
        _pullKey = host.Config.Get(PullKeyOption, DefaultPullKey);
        _range = host.Config.Get(RangeOption, DefaultRange);
        _stiffness = host.Config.Get(StiffnessOption, DefaultStiffness);
        _damping = host.Config.Get(DampingOption, DefaultDamping);
        _angularDamping = host.Config.Get(AngularDampingOption, DefaultAngularDamping);
        _maxMass = host.Config.Get(MaxMassOption, DefaultMaxMass);
        _gravity = host.Config.Get(GravityOption, DefaultGravity);
        _beam = host.Config.Get(BeamOption, true);
        _debug = host.Config.Get(DebugOption, false);
    }

    private static double Store(IModHost host, string key, double value)
    {
        host.Config.Set(key, value);
        host.Config.SaveDeferred();
        return value;
    }

    private static void PushField(LuaState lua, string key, bool value)
    {
        lua.Push(value);
        lua.SetField(-2, key);
    }

    private static void PushField(LuaState lua, string key, double value)
    {
        lua.Push(value);
        lua.SetField(-2, key);
    }

    private static bool IsDown(int key) => key != 0 && (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>
    /// Appended to CreativePlayer.lua. Only touches globals the sandbox
    /// provides - calling a stripped global would abort the chunk and make the
    /// engine fail to load the script entirely.
    /// </summary>
    private const string PlayerPatch = """
        -- The engine compiles this file once per lua_State and a world load
        -- creates about ten of them, so without this guard each compile wraps
        -- the previous wrapper and every tick runs the physics once per compile.
        -- NoclipMod learned that the expensive way; this is the same guard under
        -- a different name, so both mods can patch this file.
        if not CreativePlayer.smloaderPhysgunPatched then
        CreativePlayer.smloaderPhysgunPatched = true

        local PG_MIN_DISTANCE = 1.5     -- metres; closer than this and it is in your face
        local PG_DISTANCE_RATE = 12.0   -- metres per second while push/pull is held
        local PG_MAX_ERROR = 8.0        -- metres of spring error, clamped
        -- Effective stiffness is capped because the spring is integrated once
        -- per 40 Hz tick: k*dt much above 1.5 overshoots further every tick and
        -- fires the object into orbit rather than settling.
        local PG_MAX_STIFFNESS = 40.0
        local PG_TUNING_INTERVAL = 0.5  -- seconds between re-reads of the settings

        -- EVERY correction below is a feedback loop closed over a reading that
        -- is one tick old: the script sees the solver's state as it was at the
        -- start of the step, not as it is when the impulse lands. That single
        -- tick of latency changes the stability limit completely.
        --
        -- Without latency, a loop that removes a fraction g of the error each
        -- tick is stable for any g < 2. With one tick of it the recurrence
        -- becomes x[n+1] = x[n] - g*x[n-1], whose characteristic is
        -- z^2 - z + g = 0 and whose roots have magnitude sqrt(g) - so the real
        -- limit is g < 1, and g = 1 sits exactly on the unit circle, ringing
        -- forever rather than settling.
        --
        -- Both numbers below, and PG_MAX_SPIN_FRACTION, are gains chosen against
        -- that limit rather than the latency-free one.

        -- Fraction of the remaining gap a pinned body closes each tick. Closing
        -- all of it is the impulse that would land the body exactly on its
        -- stored position next tick - and is exactly the g = 1 that rings.
        --
        -- Swept against a simulation of the whole pin - proportional term,
        -- velocity cancellation and bias together - at zero, one and two ticks
        -- of latency, since the true figure is not observable from outside the
        -- game. Nothing at all survives two; this is chosen to be comfortably
        -- inside stability at one, which is what the game's behaviour says it
        -- actually is.
        local PG_PIN_GAIN = 0.15

        -- How much of a pinned body's velocity is cancelled each tick.
        --
        -- Cancelling all of it is the obvious thing to want and is a second way
        -- to sit on the stability limit: the velocity being cancelled was read
        -- before the impulse lands, so at full strength the correction is one
        -- tick out of phase with what it is correcting. Backing off to 0.6 was
        -- the difference between stable and divergent at one tick of latency,
        -- at every proportional gain worth using.
        local PG_PIN_VELOCITY_CANCEL = 0.6

        -- The pin learns a constant velocity bias, at this fraction of the
        -- remaining error per tick, clamped for anti-windup.
        --
        -- Proportional control alone cannot hold a body against a steady force:
        -- it settles wherever the correction happens to balance it, and that
        -- offset is exactly what you see as a frozen build sitting slightly off
        -- and drifting. The steady force here is whatever the gravity
        -- feed-forward gets wrong, and the game reports its real gravity
        -- nowhere - so rather than guess it, the pin measures it. Against a
        -- residual as large as 10 m/s^2, this takes the settled error from
        -- about 25 mm to under 0.1 mm.
        local PG_PIN_BIAS_RATE = 0.04
        local PG_PIN_MAX_BIAS = 2.0     -- metres per second, per tick

        -- Nothing the pin does may change a body's velocity by more than this in
        -- one tick, which also caps how fast a knocked build returns. The terms
        -- above cannot reach it on their own; it is here so that a surprise
        -- cannot become a launch.
        local PG_PIN_MAX_DV = 8.0       -- metres per second, per tick

        -- Ceiling on the fraction of spin any loop removes per tick, before the
        -- inertia bound scales it. The thinnest shape in the game makes that
        -- bound over-estimate by about 1.8, so the fraction has to stay under
        -- 1/1.8 to keep the loop's gain below 1. Applies to the carry and the
        -- freeze alike - without it, the spin damping slider's own maximum was
        -- enough to make a carried object diverge.
        local PG_MAX_SPIN_FRACTION = 0.4

        -- No tick may change the carried creation's velocity by more than this.
        -- The spring alone cannot exceed it (8 m of clamped error at stiffness
        -- 40 is 8 m/s at 40 Hz), so this only ever catches a hold that has
        -- already lost control - which then decays instead of launching the
        -- build over the horizon.
        local PG_MAX_DRIVE = 4.0        -- metres per second, per tick

        -- Lower bound on a body's radius of gyration squared, in m^2.
        --
        -- Spin damping is a feedback loop, and its gain is 1/inertia - which
        -- Scrap Mechanic does not expose anywhere. Scaling the angular impulse
        -- by mass, the way the game's own one-shot tumbles do (PlasmaDrill.lua),
        -- is therefore a controller over an unknown plant: for a single block,
        -- mass over-estimates inertia by a factor of ten, the correction
        -- overshoots, the spin reverses larger every tick, and after a few
        -- seconds of carrying the object is spinning hard enough to fling
        -- itself off anything it touches. That is the "it flies away and rolls"
        -- bug, and a one-shot impulse never showed it because a one-shot has no
        -- loop to close.
        --
        -- Every shape in the game is at least one 0.25 m block, so inertia is at
        -- least mass * 0.25^2 / 6 ~= mass * 0.01. Scaling by that lower bound
        -- means the applied change in spin is (bound / true inertia) * fraction
        -- * spin, which is never more than the spin itself - the loop cannot
        -- overshoot, whatever it is holding. Big creations are damped more
        -- slowly than they could be; that is the price of not knowing inertia,
        -- and it is the right side to err on.
        local PG_INERTIA_SCALE = 0.01

        -- blk_glass, from Data/Objects/Database/ShapeSets/blocks.shapeset. A
        -- creative-mode block rather than a survival asset, so it is present in
        -- every world this script runs in.
        local PG_BEAM_UUID = sm.uuid.new( "5f41af56-df4c-4837-9b3c-10781335757f" )
        local PG_BEAM_COLOR = sm.color.new( 0.25, 0.65, 1.0 )

        -- server ------------------------------------------------------------

        -- self.sv is created by BasePlayer.server_onCreate, which has already run
        -- by the time any of these fire, but self.sv.pg is ours and nothing
        -- guarantees an ordering with it, so every entry point makes it.
        local function pg_serverState( self )
            self.sv.pg = self.sv.pg or { frozen = {} }
            return self.sv.pg
        end

        -- The whole creation, not the one body the ray happened to hit.
        --
        -- Holding a single body of a jointed contraption drags the rest through
        -- its bearings, and a joint transmits force only along the axes it does
        -- not constrain. That is why a contraption followed the aim in some
        -- directions and refused in others: the directions that worked were the
        -- ones the joints happened to leave free. Lift.lua takes
        -- getCreationBodies() before it picks anything up, for the same reason.
        local function pg_creationBodies( body )
            local ok, bodies = pcall( function() return body:getCreationBodies() end )
            if ok and bodies and #bodies > 0 then
                return bodies
            end
            return { body }
        end

        -- Mass-weighted centre and velocity of everything being carried.
        --
        -- The centre is the centre of MASS, not body.worldPosition. A body's
        -- origin can sit metres from its centre of mass, and an impulse always
        -- acts at the centre of mass - so driving the origin to the target left
        -- a lever arm that rotated with the object, biasing the pull in a
        -- direction that turned as the object turned.
        local function pg_creationState( bodies )
            local centre = sm.vec3.new( 0, 0, 0 )
            local velocity = sm.vec3.new( 0, 0, 0 )
            local mass = 0
            local live = {}

            for _, body in ipairs( bodies ) do
                if sm.exists( body ) and body:isDynamic() then
                    local m = body.mass
                    centre = centre + body:getCenterOfMassPosition() * m
                    velocity = velocity + body.velocity * m
                    mass = mass + m
                    live[#live + 1] = body
                end
            end

            if mass <= 0 then
                return nil
            end

            return centre * ( 1 / mass ), velocity * ( 1 / mass ), mass, live
        end

        -- One key for the whole creation. Keying every body would run the freeze
        -- hold once per body and apply the force N times over.
        local function pg_frozenKey( bodies )
            for _, body in ipairs( bodies ) do
                if sm.exists( body ) then
                    return body.id
                end
            end
            return nil
        end

        function CreativePlayer.sv_pg_grab( self, params )
            local pg = pg_serverState( self )
            local shape = params.shape

            if not ( shape and sm.exists( shape ) ) then
                return
            end

            local body = shape.body
            if not ( body and sm.exists( body ) and body:isDynamic() ) then
                return
            end

            pg.bodies = pg_creationBodies( body )
            pg.aim = params.aim

            -- Grabbing something frozen picks it back up, rather than leaving
            -- the freeze hold fighting the carry hold over the same creation.
            for _, part in ipairs( pg.bodies ) do
                if sm.exists( part ) then
                    pg.frozen[part.id] = nil
                end
            end
        end

        function CreativePlayer.sv_pg_aim( self, params )
            pg_serverState( self ).aim = params
        end

        function CreativePlayer.sv_pg_release( self, params )
            local pg = pg_serverState( self )

            if pg.bodies and params and params.freeze then
                -- Every body's own centre of mass, because that is the point an
                -- impulse acts on and therefore the only one pg_pin can place
                -- exactly.
                local poses = {}

                for _, body in ipairs( pg.bodies ) do
                    if sm.exists( body ) and body:isDynamic() then
                        poses[#poses + 1] = {
                            body = body,
                            position = body:getCenterOfMassPosition(),
                            bias = sm.vec3.new( 0, 0, 0 ),
                        }
                    end
                end

                local key = pg_frozenKey( pg.bodies )
                if key and #poses > 0 then
                    pg.frozen[key] = poses
                end
            end

            pg.bodies = nil
            pg.aim = nil
        end

        function CreativePlayer.sv_pg_unfreeze( self, params )
            local pg = pg_serverState( self )
            local shape = params.shape

            if not ( shape and sm.exists( shape ) ) then
                return
            end

            local body = shape.body
            if not ( body and sm.exists( body ) ) then
                return
            end

            -- The entry is filed under whichever body's id came first, so
            -- pointing at any part of the creation has to be able to clear it.
            for _, part in ipairs( pg_creationBodies( body ) ) do
                if sm.exists( part ) then
                    pg.frozen[part.id] = nil
                end
            end
        end

        function CreativePlayer.sv_pg_unfreezeAll( self )
            pg_serverState( self ).frozen = {}
        end

        -- The whole mechanic, in six lines: a damped spring towards the aim
        -- point, plus enough upward impulse to cancel this tick's gravity so the
        -- body hangs instead of sagging until the spring error grows large
        -- enough to carry its own weight.
        --
        -- sm.physics.applyImpulse takes mass * a change in velocity - see
        -- BasePlayer.lua:463, which is also where the world-space third argument
        -- is confirmed - so every term below is a velocity.
        -- Returns the centre it pulled towards, or nil when there is nothing
        -- left to hold - which is how the caller learns to drop the grip.
        local function pg_hold( bodies, target, stiffness, damping, tuning, dt )
            local centre, velocity, _, live = pg_creationState( bodies )
            if not centre then
                return nil
            end

            local offset = target - centre
            local distance = offset:length()

            -- A flicked aim would otherwise be an arbitrarily large impulse.
            if distance > PG_MAX_ERROR then
                offset = offset:normalize() * PG_MAX_ERROR
            end

            -- Only the driving half is clamped. Damping always opposes the
            -- velocity it is computed from and damping * dt stays below 1, so it
            -- can never add energy however fast the creation is already going -
            -- clamping it would just blunt the one term able to recover a hold
            -- that has run away.
            local drive = offset * ( stiffness * dt )
                        + sm.vec3.new( 0, 0, tuning.gravity * dt )

            if drive:length() > PG_MAX_DRIVE then
                drive = drive:normalize() * PG_MAX_DRIVE
            end

            -- One velocity change for the creation as a whole, handed to each
            -- body in proportion to its own mass. That is what makes a
            -- contraption move as one piece instead of being towed by whichever
            -- body the ray landed on.
            local dv = drive - velocity * ( damping * dt )

            -- Fraction of the spin to remove this tick. See PG_INERTIA_SCALE for
            -- why the impulse is scaled by a lower bound on inertia rather than
            -- by mass - this loop's gain is 1/inertia, and guessing it too high
            -- is what made carried objects spin up - and PG_MAX_SPIN_FRACTION
            -- for why the ceiling is well under 1.
            local spin = math.min( tuning.angularDamping * dt, PG_MAX_SPIN_FRACTION )
                       * PG_INERTIA_SCALE

            for _, body in ipairs( live ) do
                sm.physics.applyImpulse( body, dv * body.mass, true )
                sm.physics.applyTorque( body,
                    body.angularVelocity * ( -spin * body.mass ), true )
            end

            return centre
        end

        -- Freeze pins every body of the creation to the exact place it was let
        -- go of, one by one, rather than springing the creation's centre back
        -- towards a single point.
        --
        -- The difference is what happens when you hit it. A spring answers a
        -- shove with a restoring force, so the build absorbs the energy and
        -- bobs; the harder the spring, the closer it gets to oscillating instead
        -- of settling. This computes the impulse that lands the body exactly on
        -- its stored position at the next tick - a full stop, plus the step
        -- needed to close the gap - so a hit is absorbed and undone within one
        -- tick and nothing is left over to bob with.
        --
        -- Pinning each body separately is also what stops a contraption folding
        -- at its own joints while frozen, and on anything with more than one
        -- body it holds the orientation too: several points each pinned to their
        -- own place leave the creation nothing to rotate about.
        local function pg_pin( poses, tuning, dt )
            local held = false
            local worst = 0

            for _, pose in ipairs( poses ) do
                local body = pose.body

                if sm.exists( body ) and body:isDynamic() then
                    held = true

                    local offset = pose.position - body:getCenterOfMassPosition()
                    local drift = offset:length()
                    if drift > worst then
                        worst = drift
                    end

                    -- The learned term. Integrating the error is what lets the
                    -- pin hold a body against a steady force without having to
                    -- settle at an offset first - see PG_PIN_BIAS_RATE.
                    pose.bias = pose.bias + offset * PG_PIN_BIAS_RATE
                    if pose.bias:length() > PG_PIN_MAX_BIAS then
                        pose.bias = pose.bias:normalize() * PG_PIN_MAX_BIAS
                    end

                    -- offset/dt is the velocity that would arrive exactly on the
                    -- stored position next tick; a fraction of it keeps the loop
                    -- off the ringing limit. The gravity term is a feed-forward
                    -- guess at the fall the engine adds after this impulse, and
                    -- the bias corrects whatever that guess got wrong.
                    local dv = offset * ( PG_PIN_GAIN / dt )
                             - body.velocity * PG_PIN_VELOCITY_CANCEL
                             + sm.vec3.new( 0, 0, tuning.gravity * dt )
                             + pose.bias

                    if dv:length() > PG_PIN_MAX_DV then
                        dv = dv:normalize() * PG_PIN_MAX_DV
                    end

                    sm.physics.applyImpulse( body, dv * body.mass, true )

                    -- Spin still has to go through the inertia bound - see
                    -- PG_INERTIA_SCALE - because there is no exact version of
                    -- this for rotation without knowing inertia.
                    sm.physics.applyTorque( body, body.angularVelocity
                        * ( -PG_MAX_SPIN_FRACTION * PG_INERTIA_SCALE * body.mass ), true )
                end
            end

            return held, worst
        end

        -- Numbers rather than adjectives, once a second while /physgundebug is
        -- on. "It only follows me in some directions" needs the aim, the target
        -- and the centre actually being pulled to, side by side.
        local function pg_debug( pg, tuning, dt, target, centre )
            if not tuning.debug then
                pg.debugLeft = nil
                return
            end

            pg.debugLeft = ( pg.debugLeft or 0 ) - dt
            if pg.debugLeft > 0 then
                return
            end
            pg.debugLeft = 1.0

            -- Speed and spin are the two that say whether a hold is diverging:
            -- both should sit near zero once it has settled, and a spin that
            -- climbs second on second is the angular loop misbehaving again.
            local _, velocity, mass, live = pg_creationState( pg.bodies )
            local spin = 0

            for _, body in ipairs( live or {} ) do
                spin = math.max( spin, body.angularVelocity:length() )
            end

            local direction = pg.aim.direction
            smloader.physgunLog( string.format(
                "hold: aim (%.2f, %.2f, %.2f) x %.1f m, centre (%.1f, %.1f, %.1f), "
                .. "error %.2f m, speed %.1f m/s, spin %.1f rad/s, %d bodies, %.0f kg",
                direction.x, direction.y, direction.z, pg.aim.distance,
                centre.x, centre.y, centre.z,
                ( target - centre ):length(),
                velocity and velocity:length() or 0, spin,
                #pg.bodies, mass or 0 ) )
        end

        local function pg_serverTick( self, dt )
            local pg = pg_serverState( self )
            local tuning = smloader.physgunTuning()
            local stiffness = math.min( tuning.stiffness, PG_MAX_STIFFNESS )

            if pg.bodies and pg.aim then
                local target = pg.aim.origin + pg.aim.direction * pg.aim.distance
                local centre = pg_hold( pg.bodies, target, stiffness, tuning.damping, tuning, dt )

                if centre then
                    pg_debug( pg, tuning, dt, target, centre )
                else
                    -- Welded into something static, or destroyed under us.
                    pg.bodies = nil
                    pg.aim = nil
                end
            end

            -- Frozen creations are pinned, not sprung, so none of the carry
            -- tuning applies to them.
            local worst, pinned = 0, 0

            for id, poses in pairs( pg.frozen ) do
                local held, drift = pg_pin( poses, tuning, dt )

                if held then
                    pinned = pinned + #poses
                    if drift > worst then
                        worst = drift
                    end
                else
                    pg.frozen[id] = nil
                end
            end

            -- Drift in millimetres is the number that says whether a frozen
            -- build is actually holding, and it settles rather than reading zero
            -- - so it wants reporting rather than assuming.
            if tuning.debug and pinned > 0 then
                pg.frozenDebugLeft = ( pg.frozenDebugLeft or 0 ) - dt

                if pg.frozenDebugLeft <= 0 then
                    pg.frozenDebugLeft = 1.0
                    smloader.physgunLog( string.format(
                        "frozen: worst drift %.1f mm over %d pinned bodies",
                        worst * 1000, pinned ) )
                end
            end
        end

        -- client ------------------------------------------------------------

        local function pg_clientState( self )
            self.cl.pg = self.cl.pg or { distance = 6.0 }
            return self.cl.pg
        end

        local function pg_toast( pg, text )
            pg.toast = text
            pg.toastLeft = 1.5
        end

        -- Re-read on a timer rather than per tick: the settings panel can change
        -- any of these mid-session, and half a second of staleness is invisible
        -- next to a crossing into managed code every tick for values that only
        -- move when someone drags a slider.
        local function pg_tuning( pg, dt )
            pg.tuningLeft = ( pg.tuningLeft or 0 ) - dt
            if not pg.tuning or pg.tuningLeft <= 0 then
                pg.tuning = smloader.physgunTuning()
                pg.tuningLeft = PG_TUNING_INTERVAL
            end
            return pg.tuning
        end

        local function pg_drop( self, freeze )
            local pg = pg_clientState( self )
            pg.shape = nil

            if pg.beamOn then
                pg.beam:stop()
                pg.beamOn = false
            end

            self.network:sendToServer( "sv_pg_release", { freeze = freeze } )
        end

        -- A ray landing on a bearing, piston or spring reports type "joint", not
        -- "body", and has no shape of its own - Lift.lua resolves it through the
        -- joint's shapeA. Without this the physgun refused to grab a contraption
        -- by any of its moving parts, which on most builds is most of it.
        local function pg_rayShape( result )
            if result.type == "body" then
                return result:getShape()
            end

            if result.type == "joint" then
                local ok, joint = pcall( function() return result:getJoint() end )
                if ok and joint then
                    return joint.shapeA
                end
            end

            return nil
        end

        -- The creation's mass, not the hit body's: the limit is there to stop
        -- you dragging a fortress around, and a fortress is many bodies.
        local function pg_creationMass( body )
            local ok, bodies = pcall( function() return body:getCreationBodies() end )
            local mass = 0

            for _, part in ipairs( ( ok and bodies ) or { body } ) do
                if sm.exists( part ) then
                    mass = mass + part.mass
                end
            end

            return mass
        end

        local function pg_tryGrab( self, pg, tuning, origin, direction )
            local hit, result = sm.localPlayer.getRaycast( tuning.range, origin, direction )
            if not hit then
                return
            end

            local shape = pg_rayShape( result )
            local body = shape and sm.exists( shape ) and shape.body
            if not ( body and sm.exists( body ) ) then
                return
            end

            -- Welded to the ground or sat on a lift: impulses are ignored, and
            -- saying so beats leaving the player to wonder why nothing moved.
            if not body:isDynamic() then
                pg_toast( pg, "PHYSGUN: THAT IS ANCHORED" )
                return
            end

            if tuning.maxMass > 0 and pg_creationMass( body ) > tuning.maxMass then
                pg_toast( pg, "PHYSGUN: TOO HEAVY" )
                return
            end

            pg.shape = shape
            pg.distance = math.max( ( result.pointWorld - origin ):length(), PG_MIN_DISTANCE )

            self.network:sendToServer( "sv_pg_grab", {
                shape = shape,
                aim = { origin = origin, direction = direction, distance = pg.distance },
            } )
        end

        local function pg_clientTick( self, dt )
            local pg = pg_clientState( self )
            local input = smloader.physgunPoll()

            if input.toggled then
                pg_toast( pg, input.enabled and "PHYSGUN ON" or "PHYSGUN OFF" )
            end

            if input.unfreezeAll then
                self.network:sendToServer( "sv_pg_unfreezeAll" )
                pg_toast( pg, "PHYSGUN: EVERYTHING RELEASED" )
            end

            if not input.enabled then
                if pg.shape then
                    pg_drop( self, false )
                end
                return
            end

            local origin = sm.localPlayer.getRaycastStart()
            local direction = sm.localPlayer.getDirection()
            local tuning = pg_tuning( pg, dt )

            if pg.shape and not sm.exists( pg.shape ) then
                pg_drop( self, false )
            end

            if not pg.shape then
                if input.grabDown then
                    pg_tryGrab( self, pg, tuning, origin, direction )
                elseif input.unfreezeDown then
                    local hit, result = sm.localPlayer.getRaycast( tuning.range, origin, direction )
                    local shape = hit and pg_rayShape( result )
                    if shape and sm.exists( shape ) then
                        self.network:sendToServer( "sv_pg_unfreeze", { shape = shape } )
                    end
                end
                return
            end

            if input.push then
                pg.distance = math.min( pg.distance + PG_DISTANCE_RATE * dt, tuning.range )
            end
            if input.pull then
                pg.distance = math.max( pg.distance - PG_DISTANCE_RATE * dt, PG_MIN_DISTANCE )
            end

            -- Freeze is checked before release, so freezing while grab is still
            -- held does not also fire the plain drop on the same tick.
            if input.freezeDown then
                pg_drop( self, true )
                pg_toast( pg, "PHYSGUN: FROZEN" )
            elseif input.grabUp or not input.grab then
                pg_drop( self, false )
            else
                self.network:sendToServer( "sv_pg_aim",
                    { origin = origin, direction = direction, distance = pg.distance } )
            end
        end

        -- Drawn at render rate rather than on the fixed tick: at 40 Hz the beam
        -- visibly lags the object it is attached to.
        local function pg_clientRender( self, dt )
            local pg = self.cl.pg
            if not pg then
                return
            end

            if pg.toastLeft and pg.toastLeft > 0 then
                pg.toastLeft = pg.toastLeft - dt
                sm.gui.setInteractionText( "", "", pg.toast )
            end

            local tuning = pg.tuning
            if not ( pg.shape and sm.exists( pg.shape ) and tuning and tuning.beam ) then
                if pg.beamOn then
                    pg.beam:stop()
                    pg.beamOn = false
                end
                return
            end

            if not pg.beam then
                pg.beam = sm.effect.createEffect( "ShapeRenderable" )
                pg.beam:setParameter( "uuid", PG_BEAM_UUID )
                pg.beam:setParameter( "color", PG_BEAM_COLOR )
            end

            -- Down and to the right of the eye, so the beam reads as leaving a
            -- hand rather than the middle of the camera.
            local from = sm.localPlayer.getRaycastStart()
                + sm.localPlayer.getRight() * 0.35
                - sm.vec3.new( 0, 0, 0.25 )
            local delta = pg.shape.worldPosition - from
            local length = delta:length()

            if length < 0.05 then
                return
            end

            -- A ShapeRenderable's length runs along its local z, which is what
            -- sm.vec3.getRotation is being asked for here.
            pg.beam:setPosition( from + delta * 0.5 )
            pg.beam:setRotation( sm.vec3.getRotation( sm.vec3.new( 0, 0, 1 ), delta:normalize() ) )
            pg.beam:setScale( sm.vec3.new( 0.05, 0.05, length ) )

            if not pg.beamOn then
                pg.beam:start()
                pg.beamOn = true
            end
        end

        -- wiring --------------------------------------------------------------

        -- Everything above runs inside a pcall. An error raised in one of these
        -- engine callbacks takes the whole player script with it, and a dead
        -- server_onFixedUpdate looks exactly like "the physgun does nothing" -
        -- so failures are reported to the loader log instead.
        local function pg_guard( name, fn, self, dt )
            local ok, err = pcall( fn, self, dt )
            if not ok then
                smloader.physgunLog( name .. ": " .. tostring( err ) )
            end
        end

        local pg_originalServerFixedUpdate = CreativePlayer.server_onFixedUpdate

        CreativePlayer.server_onFixedUpdate = function( self, dt, ... )
            pg_guard( "server_onFixedUpdate", pg_serverTick, self, dt or 0.025 )

            if pg_originalServerFixedUpdate then
                return pg_originalServerFixedUpdate( self, dt, ... )
            end
        end

        local pg_originalClientFixedUpdate = CreativePlayer.client_onFixedUpdate

        CreativePlayer.client_onFixedUpdate = function( self, dt, ... )
            -- Every client runs a script instance for every player in the world,
            -- and sm.localPlayer only means anything on our own. Without this
            -- guard a second player's instance polls the same keys and grabs
            -- along the first player's aim.
            if self.player == sm.localPlayer.getPlayer() then
                pg_guard( "client_onFixedUpdate", pg_clientTick, self, dt or 0.025 )
            end

            if pg_originalClientFixedUpdate then
                return pg_originalClientFixedUpdate( self, dt, ... )
            end
        end

        local pg_originalClientUpdate = CreativePlayer.client_onUpdate

        CreativePlayer.client_onUpdate = function( self, dt, ... )
            if self.player == sm.localPlayer.getPlayer() then
                pg_guard( "client_onUpdate", pg_clientRender, self, dt or 0.016 )
            end

            if pg_originalClientUpdate then
                return pg_originalClientUpdate( self, dt, ... )
            end
        end

        end -- CreativePlayer.smloaderPhysgunPatched
        """;

    /// <summary>
    /// Appended to CreativeGame.lua, which is where the game registers its own
    /// chat commands - so these sit alongside /weather and /timeofday.
    /// </summary>
    private const string GamePatch = """
        if not CreativeGame.smloaderPhysgunPatched then
        CreativeGame.smloaderPhysgunPatched = true

        function CreativeGame.cl_pg_command( self, params )
            local command = params[1]

            if command == "/physgun" then
                local enabled = smloader.physgunSetEnabled( not smloader.physgunEnabled() )
                sm.gui.chatMessage( "physgun " .. ( enabled and "ON" or "OFF" ) )

            elseif command == "/physgunrange" then
                if params[2] then
                    sm.gui.chatMessage( "physgun range "
                        .. tostring( smloader.physgunSetRange( params[2] ) ) .. " m" )
                end

            elseif command == "/physgunforce" then
                if params[2] then
                    sm.gui.chatMessage( "physgun strength "
                        .. tostring( smloader.physgunSetStiffness( params[2] ) ) )
                end

            elseif command == "/unfreezeall" then
                -- Only a request; the frozen list lives on the player script's
                -- server half, which polls for this on its next tick.
                smloader.physgunUnfreezeAll()
                sm.gui.chatMessage( "physgun: releasing everything frozen" )

            elseif command == "/physgundebug" then
                local on = smloader.physgunSetDebug( not smloader.physgunDebug() )
                sm.gui.chatMessage( "physgun diagnostics " .. ( on and "ON" or "OFF" )
                    .. " - see smloader.log" )

            elseif command == "/physgunstatus" then
                local tuning = smloader.physgunTuning()
                sm.gui.chatMessage( "physgun is "
                    .. ( smloader.physgunEnabled() and "ON" or "OFF" )
                    .. ", range " .. tostring( tuning.range )
                    .. " m, strength " .. tostring( tuning.stiffness ) )
                sm.gui.chatMessage( "Commands: /physgun  /physgunrange <m>"
                    .. "  /physgunforce <n>  /unfreezeall  /physgundebug  /physgunstatus" )
            end
        end

        local pg_originalClientOnCreate = CreativeGame.client_onCreate

        local pg_clientOnCreate = function( self, ... )
            local result
            if pg_originalClientOnCreate then
                result = pg_originalClientOnCreate( self, ... )
            end

            -- pcall'd because client_onCreate runs again on a world reload, and
            -- binding a command twice is an error that would otherwise take the
            -- rest of this callback - including another mod's - down with it.
            pcall( function()
                sm.game.bindChatCommand( "/physgun", {}, "cl_pg_command",
                    "Toggle the SMLoader physgun" )
                sm.game.bindChatCommand( "/physgunrange", { { "number", "metres", false } },
                    "cl_pg_command", "Set how far the physgun reaches" )
                sm.game.bindChatCommand( "/physgunforce", { { "number", "strength", false } },
                    "cl_pg_command", "Set how hard the physgun pulls" )
                sm.game.bindChatCommand( "/unfreezeall", {}, "cl_pg_command",
                    "Release everything the physgun froze" )
                sm.game.bindChatCommand( "/physgundebug", {}, "cl_pg_command",
                    "Log physgun aim and hold numbers to smloader.log" )
                sm.game.bindChatCommand( "/physgunstatus", {}, "cl_pg_command",
                    "Show physgun status and commands" )
            end )

            return result
        end

        -- CreativeGame.lua ends by deriving ClassicCreativeGame /
        -- CreativeCustomGame / CreativeTerrainGame, and a real world runs one of
        -- those. Because this is appended after those class() calls they already
        -- exist, so assigning only to the base class would never be reached.
        CreativeGame.client_onCreate = pg_clientOnCreate

        for _, derived in pairs( { ClassicCreativeGame, CreativeCustomGame, CreativeTerrainGame } ) do
            derived.cl_pg_command = CreativeGame.cl_pg_command
            derived.client_onCreate = pg_clientOnCreate
        end

        end -- CreativeGame.smloaderPhysgunPatched
        """;

    /// <summary>
    /// True only while the game window has focus. Keyboard state is read
    /// process-wide, so every key check has to be gated on this.
    /// </summary>
    private static uint _focusStamp;
    private static bool _focused;

    /// <summary>
    /// Recomputed at most every 100ms. The poll asks once per tick and the answer
    /// changes when the player alt-tabs, so a window-manager round trip per tick
    /// buys nothing.
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

        // A zero return means the window went away between the two calls, and
        // processId is then meaningless rather than merely wrong.
        if (GetWindowThreadProcessId(foreground, out uint processId) == 0)
            return _focused = false;

        return _focused = processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
