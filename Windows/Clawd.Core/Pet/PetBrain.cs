namespace Clawd.Core.Pet;

/// <summary>What the pet sees each frame, in world DIPs (y up).</summary>
public readonly record struct PetEnv(Box Bounds, Vec Size, Vec Mouse, double MouseSpeed, IReadOnlyList<Snack> Snacks);

/// <summary>
/// Clawd's behaviour: a state machine stepped once per frame, the Mac app's Pet.swift. Positions
/// are the window's bottom-left corner in world DIPs; the app maps them to the screen.
/// </summary>
public sealed class PetBrain(double scale)
{
    public enum State { Walk, Idle, Sleep, Drag, Fly, Hop, Wave, Pet, Dizzy, Follow, Chase, Eat, Act, Alert, Hold }

    private static readonly Random Rng = Random.Shared;
    private static double Rand(double lo, double hi) => lo + Rng.NextDouble() * (hi - lo);

    /// <summary>DIPs per sprite unit. The app adjusts it so sprite pixels land on whole device pixels.</summary>
    public double Scale { get; set; } = scale;
    public State Current { get; private set; } = State.Idle;
    public double StateTime;
    public double StateLength = 2;
    public double Dir = 1;
    public Vec Pos;               // window origin, world DIPs
    public Vec Vel;               // DIPs per second
    private double _spin;
    private double _flightTopSpeed;
    private Eyes _hopEyes = Eyes.Happy;
    public bool Following { get; private set; }

    private double _clock;
    private int _tick;
    private double _blinkIn = 3;
    private double _blinking;
    private double _rub;            // how much the cursor has been rubbing Clawd lately
    private double _happyFor;       // lingering happy face after petting or eating
    private double _startleCooldown;
    private double _mouseAway;
    private double _mouseDX;
    private double _everyCounter;
    private readonly HashSet<int> _fired = [];
    private double _boredom;        // seconds since the user last played with Clawd
    private Activity _activity = Activity.Stretch;
    internal Activity CurrentActivity => _activity;
    /// <summary>Mirrors what Orca agents are doing right now. Work starting wakes Clawd, so a
    /// sleeping pet never hides that agents are busy.</summary>
    public IReadOnlyList<Activity> WorkActivities
    {
        get => _workActivities;
        set
        {
            var started = _workActivities.Count == 0 && value.Count > 0;
            _workActivities = value;
            if (started && Current == State.Sleep) Start(Activity.Stretch);
        }
    }
    private IReadOnlyList<Activity> _workActivities = [];
    public bool Attention { get; set; }
    /// <summary>Stand under the waiting-agent card.</summary>
    public bool Holding { get; set; }
    /// <summary>Held in place (menu or chat open, pointer on Clawd).</summary>
    public bool Frozen { get; set; }
    private Snack? _meal;

    public List<Effect> Effects { get; } = [];
    public Pose Pose { get; private set; } = new();

    public double Ground(PetEnv env) => env.Bounds.MinY - (Sprite.CanvasH - Sprite.FeetY) * Scale;

    public void Enter(State s, double length = 0)
    {
        Current = s; StateTime = 0; StateLength = length; _everyCounter = 0; _fired.Clear();
    }

    public void Emit(Glyph g, double x = 9, double y = 5, double vx = 0, double life = 1.6, Rgba? color = null, double? rise = null) =>
        Effects.Add(new Effect(g, x, y, vx, 0, life, color, rise));

    /// <summary>True the first time it is asked with <paramref name="id"/> in the current state.</summary>
    private bool Once(int id) => _fired.Add(id);

    /// <summary>True once every <paramref name="interval"/> seconds while in the current state.</summary>
    private bool Every(double interval, double dt)
    {
        _everyCounter += dt;
        if (_everyCounter < interval) return false;
        _everyCounter -= interval;
        return true;
    }

    private static readonly State[] FreeStates = [State.Walk, State.Idle, State.Wave, State.Follow, State.Act, State.Hold];

    public void Step(double dt, PetEnv env)
    {
        _clock += dt; _tick += 1; StateTime += dt;
        _startleCooldown -= dt; _happyFor -= dt; _boredom += dt;

        _blinkIn -= dt;
        if (_blinkIn <= 0) { _blinking = 0.15; _blinkIn = Rand(2, 5); }
        _blinking = Math.Max(0, _blinking - dt);

        var pose = new Pose { Phase = _clock };
        pose.Eyes = _blinking > 0 ? Eyes.Closed : _happyFor > 0 ? Eyes.Happy : Eyes.Open;

        var frame = new Box(Pos.X, Pos.Y, env.Size.X, env.Size.Y);
        var bodyCenter = new Vec(frame.MidX, Pos.Y + (Sprite.CanvasH - Sprite.SpriteY - 2.5 * Sprite.PixelH) * Scale);
        _mouseDX = env.Mouse.X - bodyCenter.X;
        var mouseDist = new Vec(_mouseDX, env.Mouse.Y - bodyCenter.Y).Length;
        var mouseInside = frame.Contains(env.Mouse);
        var groundY = Ground(env);
        var onGround = Pos.Y <= groundY + 0.5;
        var free = FreeStates.Contains(Current);
        var snack = env.Snacks.MinBy(s => Math.Abs(s.CenterX - bodyCenter.X));

        // Rubbing the cursor back and forth over Clawd counts as petting.
        if (mouseInside && (free || Current == State.Pet)) _rub = Math.Min(400, _rub + env.MouseSpeed * dt);
        _rub = Math.Max(0, _rub - 200 * dt);
        if (free && _rub > 150) { _boredom = 0; Enter(State.Pet); }

        // Say hi when the cursor comes back after a while.
        if (mouseDist > 250)
        {
            _mouseAway += dt;
        }
        else
        {
            if (_mouseAway > 8 && Current is State.Walk or State.Idle)
            {
                Dir = _mouseDX >= 0 ? 1 : -1;
                Enter(State.Wave, 1.6);
                Emit(Glyph.Note, 15, 6);
            }
            _mouseAway = 0;
        }

        // A cursor whizzing past makes Clawd jump.
        if (!mouseInside && mouseDist < 130 && env.MouseSpeed > 2500 && _startleCooldown <= 0
            && Current is State.Walk or State.Idle or State.Act && onGround)
        {
            _startleCooldown = 4;
            Vel.Y = 300; _hopEyes = Eyes.Wide;
            Enter(State.Hop);
            Emit(Glyph.Bang, 9.7, 4);
        }

        // A trick or celebration plays out first; it ends in PickNext, which comes back here.
        if (Holding && free && Current is not (State.Hold or State.Act)) Enter(State.Hold);

        // Held in place: no walking on the spot, no running off after a snack yet.
        if (Frozen && Current is State.Walk or State.Follow or State.Chase) Enter(State.Idle, 1);

        if (snack is not null && (free || Current == State.Sleep) && !Frozen && Current != State.Chase)
        {
            _boredom = 0;
            Emit(Glyph.Bang, 9.7, 4);
            Enter(State.Chase);
        }

        double moveX = 0;
        void Scuttle(double speed)
        {
            moveX = Dir * speed * dt;
            pose.Legs = _tick / 4 % 2 == 0 ? Legs.Stand : Legs.Step;
            pose.Bob = _tick / 4 % 2 == 0 ? 0 : -0.25;
            pose.Look = Dir;
        }
        var atLeft = Pos.X <= env.Bounds.MinX + 1;
        var atRight = Pos.X + env.Size.X >= env.Bounds.MaxX - 1;

        switch (Current)
        {
            case State.Walk:
                if ((Dir < 0 && atLeft) || (Dir > 0 && atRight)) Dir = -Dir;
                Scuttle(30);
                if (StateTime > StateLength) PickNext();
                break;

            case State.Idle:
                pose.Look = _mouseDX / 150;
                if (StateTime > StateLength) PickNext();
                break;

            case State.Follow:
                if (!Following) { Enter(State.Idle, 1); break; }
                if (Math.Abs(_mouseDX) > 35)
                {
                    Dir = _mouseDX > 0 ? 1 : -1;
                    Scuttle(90);
                }
                else
                {
                    pose.Look = _mouseDX / 60;
                    if (env.Mouse.Y > bodyCenter.Y + 40) pose.ArmsUp = _tick / 6 % 2 == 0;
                }
                break;

            case State.Sleep:
                pose = Activities.SleepPose(StateTime);
                if (StateTime > 5 && Every(1.5, dt)) Emit(Glyph.Z, 15, 11);
                if (StateLength > 0 && StateTime > StateLength) Start(Activity.Stretch);
                break;

            case State.Drag:
                pose.Legs = Legs.Dangle;
                pose.ArmsUp = _tick / 4 % 2 == 0;
                pose.Eyes = Eyes.Wide;
                pose.Look = 0;
                break;

            case State.Fly:
                Vel.Y -= Physics.Gravity * dt;
                Pos.X += Vel.X * dt;
                Pos.Y += Vel.Y * dt;
                _flightTopSpeed = Math.Max(_flightTopSpeed, Vel.Length);
                if (Pos.X < env.Bounds.MinX) { Pos.X = env.Bounds.MinX; Vel.X = Math.Abs(Vel.X) * 0.6; }
                if (Pos.X + env.Size.X > env.Bounds.MaxX) { Pos.X = env.Bounds.MaxX - env.Size.X; Vel.X = -Math.Abs(Vel.X) * 0.6; }
                if (Pos.Y + env.Size.Y > env.Bounds.MaxY) { Pos.Y = env.Bounds.MaxY - env.Size.Y; Vel.Y = -Math.Abs(Vel.Y) * 0.5; }
                if (_flightTopSpeed > 900) _spin += Vel.X * dt * 0.012;
                pose.Legs = Legs.Dangle;
                pose.ArmsUp = true;
                pose.Eyes = Eyes.Wide;
                pose.Rotation = _spin;
                if (Pos.Y <= groundY)
                {
                    Pos.Y = groundY;
                    var impact = -Vel.Y;
                    if (impact > 650)
                    {
                        Vel.Y = impact * 0.4;
                        Vel.X *= 0.6;
                    }
                    else
                    {
                        Vel = Vec.Zero; _spin = 0;
                        if (_flightTopSpeed > 1500) Enter(State.Dizzy, 2.5); else Enter(State.Idle, 1.5);
                    }
                }
                break;

            case State.Hop:
                pose.ArmsUp = _tick / 3 % 2 == 0;
                pose.Eyes = _hopEyes;
                if (onGround && Vel.Y <= 0 && StateTime > 0.1) Enter(State.Idle, Rand(1, 2));
                break;

            case State.Wave:
                pose.ArmsUp = _tick / 5 % 2 == 0;
                pose.Eyes = Eyes.Happy;
                pose.Look = Dir;
                if (StateTime > StateLength) PickNext();
                break;

            case State.Pet:
                pose.Eyes = Eyes.Happy;
                pose.Bob = Math.Sin(_clock * 8) > 0 ? 0 : 0.15;
                if (Every(0.35, dt)) Emit(Glyph.Heart, Rand(3, 13), 6);
                if (_rub < 10) { _happyFor = 2; Enter(State.Idle, 2); }
                break;

            case State.Dizzy:
                pose.Eyes = Eyes.Dizzy;
                pose.Bob = Math.Sin(_clock * 6) > 0 ? 0 : 0.15;
                if (StateTime > StateLength) { Emit(Glyph.Question, 9.2, 4); Enter(State.Idle, 2); }
                break;

            case State.Chase:
            {
                if (snack is null) { Enter(State.Idle, 1); break; }
                // Stop with the snack right at the claw tip instead of on top of it.
                var dx = snack.CenterX - bodyCenter.X;
                if (Math.Abs(dx) > 55)
                {
                    Dir = dx > 0 ? 1 : -1;
                    Scuttle(110);
                }
                else if (snack.Grounded)
                {
                    Dir = dx > 0 ? 1 : -1;
                    _meal = snack;
                    Enter(State.Eat);
                }
                else
                {
                    // Snack is still falling or being dangled above: beg for it.
                    pose.ArmsUp = _tick / 4 % 2 == 0;
                    pose.Eyes = Eyes.Wide;
                    if (onGround && snack.Held && Rng.NextDouble() < dt * 0.8) Vel.Y = 420;
                }
                break;
            }

            case State.Eat:
                if (_meal is not { Grounded: true } meal || meal.Gone || Math.Abs(meal.CenterX - bodyCenter.X) >= 70) { Enter(State.Chase); break; }
                pose.Mouth = _tick / 4 % 2 == 0 ? 1 : 0;
                pose.Eyes = Eyes.Happy;
                pose.Look = Dir;
                if (Every(0.6, dt))
                {
                    meal.Bites += 1;
                    var side = Dir > 0 ? 15.0 : 3.0;
                    Emit(Glyph.Crumb, side, 13, Dir * 2, 0.6);
                    Emit(Glyph.Crumb, side, 13, Dir * 4, 0.6);
                    if (meal.Bites >= 3)
                    {
                        meal.Consume();
                        _meal = null;
                        Emit(Glyph.Heart, 5, 6); Emit(Glyph.Heart, 11, 5);
                        _happyFor = 2.5;
                        Enter(State.Idle, 2);
                    }
                }
                break;

            case State.Hold:
                // Arms up under the card so it reads as "this one's for you".
                pose.ArmL = 0.5; pose.ArmR = 0.5;
                pose.Look = _mouseDX / 150;
                if (!Holding) PickNext();
                break;

            case State.Alert:
            {
                pose.Eyes = Eyes.Wide;
                var flap = _tick / 3 % 2 == 0;
                pose.ArmL = flap ? 0.5 : 1.5;
                pose.ArmR = flap ? 1.5 : 0.5;
                if (onGround && Once((int)(StateTime / 0.8)))
                {
                    Vel.Y = 320;
                    Emit(Glyph.Bang, 9.7, 4, life: 0.8);
                }
                if (StateTime > StateLength) PickNext();
                break;
            }

            case State.Act:
            {
                var t = StateTime;
                var blink = pose.Eyes == Eyes.Closed;
                pose = Activities.Pose(_activity, t, Dir);
                if (blink && pose.Eyes == Eyes.Open) pose.Eyes = Eyes.Closed;
                switch (_activity)
                {
                    case Activity.Type:
                        if (Every(0.45, dt)) Emit(Glyph.Bit, Rand(7, 13), 12, life: 1);
                        break;
                    case Activity.Build:
                        if (pose.Prop == new Prop(PropKind.Hammer, false) && Once((int)(t * 2.2)))
                        {
                            Emit(Glyph.Star, 11.5, 15, -3, 0.5);
                            Emit(Glyph.Star, 13, 15, 3, 0.5);
                        }
                        break;
                    case Activity.Groove:
                        if (Every(0.6, dt)) Emit(Glyph.Note, Rand(2, 16), 7);
                        break;
                    case Activity.Sweep:
                        if ((Dir < 0 && atLeft) || (Dir > 0 && atRight)) Dir = -Dir;
                        moveX = Dir * 12 * dt;
                        if (Every(0.4, dt)) Emit(Glyph.Dust, 14, 18, -Dir * 2, 0.8);
                        break;
                    case Activity.Coffee:
                        if (Every(0.5, dt))
                        {
                            var raised = pose.Prop == new Prop(PropKind.Mug, true);
                            Emit(Glyph.Steam, raised ? 14.2 : 16.9, raised ? 12.4 : 13.4, life: 1);
                        }
                        break;
                    case Activity.Celebrate:
                        if (Every(0.07, dt))
                        {
                            Rgba[] colors = [Sprite.Rgb(235, 90, 80), Sprite.Rgb(90, 150, 240), Sprite.Rgb(250, 200, 70), Sprite.Rgb(120, 220, 140), Sprite.Rgb(237, 92, 115)];
                            Emit(Glyph.Crumb, Rand(1, 19), 0, Rand(-2, 2), 2.5, colors[Rng.Next(colors.Length)], -Rand(4, 8));
                        }
                        if (onGround && Vel.Y <= 0 && Once((int)(t / 0.7))) Vel.Y = 350;
                        break;
                    case Activity.LookAround:
                        if (t > 2 && Once(0)) Emit(Glyph.Question, 9.2, 4);
                        break;
                    case Activity.Sneeze:
                        if (t > Activities.SneezeAt && Once(0))
                        {
                            Vel.Y = 250;
                            Emit(Glyph.Dust, 9, 15, -6, 0.6);
                            Emit(Glyph.Dust, 9, 15, 6, 0.6);
                            Emit(Glyph.Bang, 15, 6, life: 1);
                        }
                        break;
                }
                if (t > StateLength)
                {
                    if (_activity == Activity.Think)
                    {
                        Vel.Y = 380; _hopEyes = Eyes.Happy;
                        Enter(State.Hop);
                        Emit(Glyph.Note, 9.5, 4);
                    }
                    else
                    {
                        PickNext();
                    }
                }
                break;
            }
        }

        // Shared vertical physics for everything except flying and being held.
        if (Current is not (State.Fly or State.Drag))
        {
            if (!onGround || Vel.Y > 0)
            {
                Vel.Y -= Physics.Gravity * dt;
                Pos.Y += Vel.Y * dt;
            }
            if (Pos.Y <= groundY) { Pos.Y = groundY; Vel.Y = Math.Max(Vel.Y, 0); }
            Pos.X = Math.Clamp(Pos.X + moveX, env.Bounds.MinX, Math.Max(env.Bounds.MinX, env.Bounds.MaxX - env.Size.X));
        }

        pose.Attention = Attention && Current != State.Alert;
        Pose = pose;
        for (var i = 0; i < Effects.Count; i++)
        {
            var e = Effects[i];
            e.Age += dt;
            Effects[i] = e;
        }
        Effects.RemoveAll(e => e.Age > e.Life);
    }

    public void PickNext()
    {
        if (Holding) { Enter(State.Hold); return; }
        if (Following) { Enter(State.Follow); return; }
        if (WorkActivities.Count > 0)
        {
            // While agents work Clawd only mirrors them; tricks are for idle time.
            // Several agents at once means juggling half the time; otherwise copy one agent's tool,
            // so what each is doing still shows now and then.
            Start(WorkActivities.Count >= 2 && Rng.Next(2) == 0 ? Activity.Juggle : WorkActivities[Rng.Next(WorkActivities.Count)]);
            return;
        }
        if (WorkActivities.Count == 0 && _boredom > 60 && Rng.NextDouble() < 0.3)
        {
            Enter(State.Sleep, Rand(30, 60));
            return;
        }
        var r = Rng.NextDouble();
        if (r < 0.3 && !Frozen)
        {
            Dir = Rng.Next(2) == 0 ? 1 : -1;
            Enter(State.Walk, Rand(2, 6));
        }
        else if (r < 0.5)
        {
            Enter(State.Idle, Rand(1.5, 4));
        }
        else
        {
            var others = Activities.Resting.Where(a => a != _activity).ToArray();
            Start(others[Rng.Next(others.Length)]);
        }
    }

    public void Start(Activity a)
    {
        _activity = a;
        if (a == Activity.Sweep) Dir = Rng.Next(2) == 0 ? 1 : -1;
        Enter(State.Act, a.Duration());
    }

    // MARK: Input

    public void Poked()
    {
        _boredom = 0;
        if (Current == State.Sleep)
        {
            Emit(Glyph.Bang, 9.7, 4);
            Vel.Y = 300; _hopEyes = Eyes.Wide;
            Enter(State.Hop);
            return;
        }
        Vel.Y = 400; _hopEyes = Eyes.Happy;
        Enter(State.Hop);
        Emit(Glyph.Heart, 7.5, 6);
    }

    public void ToggleFollow()
    {
        Following = !Following;
        _boredom = 0;
        Emit(Following ? Glyph.Note : Glyph.Question, 9.5, 4);
        Enter(Following ? State.Follow : State.Idle, 2);
    }

    /// <summary>An Orca agent needs the user: make a scene about it.</summary>
    public void Alert()
    {
        if (Current is State.Drag or State.Fly) return;
        Enter(State.Alert, 3.5);
    }

    public void Celebrate()
    {
        if (Current is State.Drag or State.Fly or State.Eat or State.Alert) return;
        Start(Activity.Celebrate);
    }

    public void Grabbed()
    {
        _rub = 0; _boredom = 0;
        Enter(State.Drag);
    }

    public void Thrown(Vec v)
    {
        var speed = v.Length;
        var k = speed > 3000 ? 3000 / speed : 1;
        Vel = new Vec(v.X * k, v.Y * k);
        _flightTopSpeed = speed * k;
        _spin = 0;
        Enter(State.Fly);
    }

    public void Sleep() => Enter(State.Sleep);
}
