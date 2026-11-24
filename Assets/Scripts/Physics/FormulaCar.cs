// FormulaCar.cs — formula car vehicle model, a line-by-line port of the web prototype's physics.js.
// Pure C# (no UnityEngine), so it runs identically in Unity and in the command-line test harness.
// Units are SI (m, kg, s, N, rad). Doubles are used on purpose: results match the JS version bit-for-bit
// in normal driving, and Unity reads the outputs as floats.
//
// Physics frame: world x/y plane, heading 0 = +x, positive yaw = counter-clockwise (left).
// Car frame: vx forward, vy left, r yaw rate.
// Calibrated to the 2026 Formula 1 car (see CarConfig for sources and tuning notes).
using System;

namespace FormulaSim
{
    public enum TcMode { Off, Low, High }

    // Defaults = 2026 Formula 1 car. Sources: FIA 2026 technical regulations as published by the FIA, Honda and
    // the teams (mass, wheelbase, width, power unit, energy store, MGU-K ramp-down), Pirelli (tyre sizes).
    // Values marked "est." are not published; they were fitted so the model reproduces measured F1 performance:
    // the 2026 pole laps at Monaco (1:12.051) and Spa (1:44.361), speed-trap figures and typical g-forces.
    [Serializable]
    public class CarConfig
    {
        public double mass = 783;            // kg: 768 FIA minimum (with driver) + ~15 kg fuel (qualifying trim)
        public double inertiaZ = 1100;       // kg m^2 yaw inertia (est.)
        public double wheelbase = 3.40;      // m (FIA maximum, all teams use it)
        public double frontWeight = 0.45;    // static weight on front axle (est., regulation window ~44-46 %)
        public double cgHeight = 0.27;       // m (est.)
        public double wheelRadius = 0.355;   // m (Pirelli 2026 rear: 710 mm diameter)
        public double mu = 2.10;              // tyre friction at static load (est., see calibration in Tools/PhysicsCheck)
        public double rearGrip = 1.08;       // wider rear tyres (375 mm vs 280 mm)
        public double loadExp = 0.8;         // max tyre force ~ load^0.8 (tyre load sensitivity)
        public double tireB = 14.5, tireC = 1.6, tireE = 0.3; // magic formula, peak ~6.7 deg
        public double slipGrowth = 0.3;      // peak slip angle grows with load
        public double peakRatio = 0.11;      // longitudinal slip ratio at peak traction
        public double wheelInertia = 1.8;    // kg m^2, both rear wheels + tyres + discs (est.)
        public double engineInertia = 0.045; // kg m^2, felt at the wheel x ratio^2
        public double track = 1.58;          // m, track width (1.9 m car, 280/375 mm tyres)
        public double latTransferFront = 0.5;
        public double cdA = 1.10;            // drag area m^2, wings closed (corner mode) (est.)
        public double clA = 3.9;             // downforce area m^2, corner mode (est.)
        public bool activeAero = true;       // 2026 active aero: wings open on straights (straight mode)
        public double cdAX = 0.70, clAX = 2.5; // drag / downforce area with wings open (est.)
        public double aeroFront = 0.45;
        public double rollRes = 0.015;
        // combustion engine: 1.6 V6 turbo, ~400 kW from 10,500 rpm (fuel-flow limited), 15,000 rpm allowed
        public double peakTorque = 365;      // Nm at the crank
        public double[] torqueRpm = { 4000, 6000, 8000, 10500, 12000, 13000 };
        // low revs: no MGU-H in 2026, so the turbo is off boost until the revs come up (est., fitted to the measured
        // 2.8-2.9 s 0-100 km/h of the 2026 cars; above 50 km/h the MGU-K fills the hole)
        public double[] torqueFrac = { 0.30, 0.60, 0.96, 1.0, 0.875, 0.80 };
        public double redline = 12000, idleRpm = 4000;
        public double[] gearTopSpeeds = { 130, 165, 200, 232, 262, 292, 322, 350 }; // km/h at the limiter, 8 gears (est.)
        public double driveEff = 0.94;
        // hybrid: MGU-K 350 kW, 4 MJ battery, 8.5 MJ harvest per lap (FIA)
        public double ersPower = 350e3;      // W
        public double ersCapacity = 4e6;     // J usable
        public double ersHarvestLap = 8.5e6; // J per lap
        public double ersLiftPower = 100e3;  // W harvested when coasting off-throttle (est.)
        // FIA 2026 C5.2.12: at a standing start the MGU-K may only be used once the car has reached 50 km/h
        public double ersStartKmh = 50;
        // energy management (est.): 8.5 MJ a lap pays for only ~24 s of 350 kW, so the cars spend it out of the
        // corners and fade it out long before the FIA ramp-down. Full power to ersFadeFrom km/h, then linearly down
        // to ersFadeFrac of it at ersFadeTo km/h. Fitted to measured 2026 cars: 100-200 km/h in ~2.0 s but
        // 200-300 km/h in ~5 s (W17, MCL40).
        public double ersFadeFrom = 200, ersFadeTo = 300, ersFadeFrac = 0.15;
        // energy management over the lap (2026): the same simple rule for the player and the AI, so the battery lasts
        // the lap as on the real cars. At full throttle the MGU-K deploys while the speed is below
        // ersDepKmh + ersDepSoc x charge (0..1), and harvests ("superclipping", ICE power turned into charge while the
        // throttle stays flat) above ersClipKmh + ersClipSoc x charge, ramping in over ersClipRamp km/h. The lower the
        // battery, the earlier deployment stops and clipping starts, so a full battery is spent out of the slow corners
        // and the speed peaks in the first half of a long straight, then plateaus or falls (2026 speed traces:
        // Monza 2026 quali top 341 km/h, ~11 km/h lost before the brake point; China 333 -> 306; FIA after Miami:
        // superclip up to 350 kW). At part throttle the ICE keeps running harder than the driver asks and the MGU-K
        // takes the difference (up to ersPartPower); the force at the wheels is the same as before.
        public bool ersManage = true;
        public double ersDepKmh = 250, ersDepSoc = 500, ersDepRamp = 25;    // km/h
        public double ersClipKmh = 290, ersClipSoc = 300, ersClipRamp = 15; // km/h
        public double ersClipPower = 350e3;  // W (FIA 2026 after the Miami changes; 250 kW before)
        public double ersPartPower = 350e3;  // W harvested at part throttle (2026 cars keep the ICE near full fuel flow)
        // where to deploy: FormulaCar.deployMap (-1..2, from RacingLine.deploy at the car's place). 0..1 scales the
        // deploy power (short bursts between corners get little); 1 = the default map above (with the fade, as in the
        // measured standing starts); 1..2 lifts the fade towards the FIA ramp-down where a long straight follows, so the
        // energy goes where it buys the most lap time; below 0 is a superclip zone (the last seconds of a long
        // straight, sized per circuit so the lap's energy balances). Below 1 the map is relaxed above ersMapSoc of
        // charge (a full battery could not harvest any more anyway).
        public double ersMapSoc = 0.6;
        public double ersClipMinKmh = 250;   // a negative deployMap (superclip zone) harvests above this speed
        // 2026 Overtake Mode (replaces DRS; FormulaCar.otArmed / otOn, armed by OvertakeRule): within 1 s of the car ahead
        // at a detection point, the driver may deploy up to otEnergy extra MGU-K energy a lap (its own allowance, not
        // from the battery), and full 350 kW is allowed up to otFullKmh (then down to zero at otZeroKmh) instead of the
        // normal ramp-down from 290 km/h. Boost button (FormulaCar.boostOn): full MGU-K power from the battery, whatever
        // the energy-management map says, while the charge is above boostMinSoc. Both only flat out.
        public double otEnergy = 0.5e6;      // J per lap
        public double otFullKmh = 337, otZeroKmh = 355;
        public double boostMinSoc = 0.05;
        public double shiftTime = 0.03;
        // ---- feel: turbo lag, tyre relaxation, downshift kick (all 0 = off, as in the JS prototype) ----
        // turbo without MGU-H: boost follows the throttle with a lag (d boost/dt = (throttle - boost) / tau); off boost
        // the engine gives turboNa of its torque. The MGU-K covers the hole while the battery has charge.
        public double turboLag = 0.35;       // s to spool up (est.; falls away 3x slower off throttle)
        public double turboNa = 0.45;        // share of the torque left with no boost (est.)
        // tyre relaxation: a tyre needs to roll about one radius before the side force follows a new slip angle
        // (Pacejka, Tyre and Vehicle Dynamics ch. 5): d alpha'/dt = (v / relaxLen) (alpha - alpha')
        public double relaxLen = 0.35;       // m
        // downshift: the engine is spun up through the rear tyres; the throttle blip covers most of it and the rest,
        // engine inertia x rev jump x ratio / wheel radius x downshiftKick (N s), brakes the rear axle for ~0.06 s
        public double downshiftKick = 0.3;
        public double clutchEngage = 13;     // m/s (47 km/h) by which the launch clutch is fully engaged
        // launch: the clutch slips (anti-stall) until the wheels reach clutchBite rpm at full pedal, so the engine is
        // not dragged down into the turbo hole (~4200 rpm at 45 km/h: the acceleration fell from 1.3 to 0.45 g and
        // then kicked back at 50 km/h). 0 = old behaviour (revs fall to idle by clutchEngage).
        public double clutchBite = 4800;     // rpm (est.)
        public double launchRpm = 9000;         // rpm the launch map holds at rest at full pedal (0 = idle + half the redline)
        // s over which the MGU-K comes in at ersStartKmh after a standing start (eased in: at 50 km/h a tenth of its
        // 350 kW is already as much force as the rear tyres can take, so a step was a 0.6 g kick)
        public double ersStartRamp = 1.2;
        public double brakeForce = 46000;    // N total at full pedal (enough for ~5 g with downforce)
        public double brakeBias = 0.57;      // front share
        // carbon-carbon brakes (Brembo: working window about 350-1000 C): weak when cold, fading when far too hot
        public bool brakeTemps = true;
        public double brakeStart = 300, brakeAmbient = 30;   // deg C
        public double brakeHeatCap = 2400;                    // J/K per axle (discs + pads), ~500 K rise from a 340->80 km/h stop
        public double brakeCool0 = 30, brakeCoolV = 1.2;      // W/K at rest, plus per m/s (about 20 s time constant at 300 km/h)
        // glowing carbon discs also radiate: emissivity x area per axle (m^2; ~0.85 x the faces and rims of two
        // 278-330 mm discs, est.). At 1000 C that is about 40 kW per axle, as much as the ducts take at 150 km/h.
        public double brakeRadA = 0.25;
        public double maxLockDeg = 22;       // Monaco-spec steering lock
        public double speedSens = 0.5;      // steering aid: lock = full / (1 + speedSens (v/30)^2). 0 = full lock at any speed
        public double csFull = 0.07;        // slide angle (rad) at which countersteer gets full lock again
        public double steerSpeed = 3.0;
        public double steerReturn = 8.0;
        public bool csOpen = true;           // open up to full lock when countersteering
        // ---- chassis detail (suspension, ride-height aero, camber, tyre heat, differential, slipstream) ----
        // Teams do not publish these; the values are typical figures from F1 engineering literature and Pirelli's
        // prescriptions, chosen so the whole-car results still match the measured performance above.
        public bool chassis = true;
        public double rideF0 = 0.032, rideR0 = 0.060;     // m, static ride heights (front low, rear higher = rake)
        public double springF = 300e3, springR = 260e3;   // N/m per axle at the wheels (springs + heave element + tyres)
        public double heaveRate = 18;                     // 1/s, how fast the body settles (damping)
        public double rollStiff = 1.5e6;                  // Nm/rad, both axles together (about 0.35 deg of roll at 5 g)
        public double rideDesign = 0.030, rideGain = 0.25, rideStall = 0.008; // ground effect: lower = more downforce, until the floor stalls
        public double rakeGain = 2.0;                     // aero balance moves forward by this much per metre of extra rake (nose down)
        public double yawSens = 0.4;                      // downforce lost per radian of slide angle
        public double camberF = -3.25, camberR = -1.75;   // deg, static camber (Pirelli limits are about -3.5 / -2.0)
        public double toeF = -0.08, toeR = 0.10;          // deg per wheel (front toe-out, rear toe-in)
        public Compound compound = Compound.Soft;
        public double tyreLife = 1.0;                     // multiplies every compound's life (1 = a Soft lasts about 25 laps of Spa)
        public double wearPerJ = 1.7e-8;                  // tread worn per joule of sliding energy (soft); calibrated in Tools/PhysicsCheck
        public double tyreStart = 70, tyreOpt = 100, tyreWindow = 30, tyreLoss = 0.06; // deg C: blanket temp, best temp, and grip lost one window away
        public double trackTemp = 40;                     // deg C
        public double tyreHeatCap = 22e3;                 // J/K per axle (tread and carcass that follow the surface)
        public double tyreHeatShare = 0.45;               // part of the sliding energy that goes into the tyre (rest into the road)
        public double tyreCool0 = 50, tyreCoolV = 1.85;    // W/K at rest, plus per m/s of air and road speed
        public bool frontSlipHeat = true;                 // front braking heat from the slip ratio (see Step)
        public double tyreFrontSize = 0.9;                // front tyres' heat capacity and cooled area against the rears' (280 vs 375 mm wide; with frontSlipHeat)
        public double diffPreload = 60, diffPower = 0.15, diffCoast = 0.25; // differential: Nm preload, locking per unit of drive / overrun torque
        public double towDrag = 0.30, towDown = 0.10;     // slipstream right behind another car: drag and downforce lost
        // floor strikes: with the plank on the road the ground, not the tyres, carries part of the downforce (and the
        // floor's seal is upset), and the skid block rubs: tyre load lost at a hard strike, plank friction (est.)
        public double floorLoss = 0.05, plankMu = 0.3;
        public TcMode tc = TcMode.Low;       // driver aids (not allowed in real F1; kept for touch controls)
        public bool abs = true, autoShift = true;
        // ---- race detail: fuel, tyre surface heat, brake setting, wind, kerbs ----
        // fuel load: `mass` already holds fuelRef kg; the car carries fuelStart kg at the start and burns it as the
        // engine works. FIA 2026: fuel energy flow max 3000 MJ/h (about 70 kg/h of the sustainable fuel) and at most
        // 70 kg for the race. Weight costs about 0.03-0.04 s a lap per kg (The Race: 10 kg = 0.3-0.4 s).
        public bool fuel = true;
        public double fuelRef = 15;          // kg of fuel included in `mass` (qualifying trim)
        public double fuelStart = 15;        // kg at the start (a race: set up to 70)
        public double fuelFlow = 70;         // kg/h at full combustion power (FIA 2026 energy-flow limit)
        public double fuelFront = 0.50;      // where the tank sits, as a front-axle share (est.: behind the driver, a bit ahead of the CG)
        public double fuelHeight = 0.20;     // m, height of the fuel's centre of mass (est.: tank on the floor)
        public double fuelGyr = 0.35;        // m, radius of gyration of the fuel about its own centre (est.)
        // tyre surface: a thin tread layer that heats in a fraction of a second when sliding or locking and passes
        // the heat on to the carcass (tyreTF/TR). Grip sees part of the surface temperature, so a lock-up or a
        // slide costs grip for a moment and then recovers as the heat soaks into the carcass.
        public bool tyreSurface = true;
        public double tyreSurfCap = 1500;    // J/K per axle (est.: ~1 mm of tread on two tyres)
        public double tyreSurfLink = 2000;   // W/K surface-to-carcass conduction per axle (est.: ~0.75 s time constant)
        public double tyreSurfGrip = 0.3;    // share of the surface-carcass difference that the grip feels (0.5 made the trained AI slide off more)
        public double tyrePressF = 25, tyrePressR = 23; // psi set at blanket temperature (typical Pirelli starting pressures, est.)
        // brakes: bias migration moves the balance rearwards as the pedal is eased (front % = brakeBias - migration x
        // (1 - pedal)); engineBrake scales the overrun engine braking on the rear axle (steering-wheel "EB" map)
        public double brakeMigration = 0;    // 0 = off; real cars use about 0.02-0.05
        public double engineBrake = 1;       // 1 = as calibrated; more = more rear braking on entry (less stable)
        // brake-by-wire (F1 since 2014): the rear brakes are electronically controlled, so the rear hydraulic torque is
        // what is left of the rear's share after engine braking (the MGU-K's harvest is already inside that share), and
        // the teams' BBW maps move the balance forward as the speed and the downforce fall through a braking zone
        // (bbwShift at a standstill, none from bbwShiftKmh up). Without it the rears locked first at a 57 % balance;
        // real cars lock the fronts first.
        public bool bbw = true;
        // speed profile (RacingLine): brake at brakeUse of the full tyre grip, not brakeUse x the cornering safety
        // margin as well (that left the AI braking at 3.4-3.9 g against 5.6 g available)
        public bool brakeFullGrip = true;
        public double bbwShift = 0.04, bbwShiftKmh = 250;
        // wind (the game sets car.windX / windY): side force and its yaw moment act at the centre of pressure
        public double sideCyA = 4.5;         // m^2 per radian of aero yaw (est.: side force slope x frontal area)
        public double sideCp = -0.3;         // m, centre of pressure ahead (+) of the CG (est.: behind, so the car weathervanes)
        // kerbs: a kerb under one side tilts the car, so the roll springs move load onto the kerb side
        public bool kerbTransfer = true;
        public double kerbLift = 0.5;        // share of a surface's bump height that lifts the tyre on average (est.)
        public double kerbRaise = 0.1;       // share of a raised kerb's lift (Track.KerbLift) that rolls the chassis; the tyre's sidewall takes the rest (est.)
        // tyres that ran through grass, gravel or astroturf pick up dirt and grip less until it wears off
        public double dirtLoss = 0.08, dirtClear = 400; // grip lost when fully dirty; metres driven to clear about 63 %
        // ---- damage (FormulaCar.Damage, FormulaCar.ApplyImpact) ----
        // Off by default, so the golden test and older set-ups are unchanged; the game turns it on for "Damage: Full".
        // Impacts are always recorded in car.damage (the model shows them in "Visual only"); only with this flag do
        // they change the physics. Magnitudes: one "point" of downforce = 0.01 of the lift coefficient, about 0.18 %
        // of a ~5.5 CL car (Autosport, "What F1 technical directors mean when they talk about points of downforce").
        //  * front wing: Leclerc's endplate damage at China 2025 cost 20-30 points (~4-5 % of the total = ~10-12 %
        //    of the front axle's share); Albon losing an endplate at Baku 2023 cost ~0.3 s a lap (Vowles). One side
        //    fully gone here = 30 % of the front downforce (an endplate is about 0.4 of a side = 12 %).
        //  * floor: Verstappen's floor damage (Hungary 2023, debris) cost up to 100 points (~18 %), Bottas'
        //    (Hungary 2021) ~50 points; a fully wrecked floor here = 18 % of all downforce.
        //  * rear wing / rear bodywork: Hamilton's kerb damage (Austria 2020) cost ~30 points; the whole rear wing is
        //    taken as ~35 % of the rear axle's downforce and ~12 % of the drag (est.).
        //  * suspension / steering: a bent wishbone or track rod puts the wheel out of line by up to dmgToe degrees
        //    (setup toe is only 0.05-0.2 deg, so under a degree is already a strong pull) plus a steering offset (est.).
        //  * puncture: a deflated tyre keeps about a third of its grip, drops the corner ~5 cm (est.).
        public bool damage = false;
        public double dmgFrontWing = 0.30;   // front downforce lost per side of the front wing at damage 1
        public double dmgRearWing = 0.35;    // rear downforce lost at rear-wing damage 1
        public double dmgRearDrag = 0.12;    // drag lost at rear-wing damage 1 (less wing); broken front parts add dmgFrontDrag per side
        public double dmgFrontDrag = 0.02;
        public double dmgFloor = 0.18;       // total downforce lost at floor damage 1
        public double dmgToe = 0.3;          // deg of toe at suspension damage 1 (the wheel turns out on the damaged side)
        public double dmgSteer = 0.15;       // deg of steering offset at front suspension damage 1 (~2 deg at the steering wheel; at 250 km/h 0.3 deg of road-wheel angle is ~0.7 g)
        public double dmgRearToe = 1.0;      // the rear toe as a share of dmgToe
        public double dmgSuspGrip = 0.08;    // grip lost at a corner with suspension damage 1 (camber and geometry out)
        public double dmgSpeed = 11;         // m/s of closing speed above dmgMinSpeed that wrecks a part (about 40 km/h)
        public double dmgMinSpeed = 2.5;     // m/s (9 km/h), lighter touches do nothing (game-friendly: AI nudges in the pack)
        public double punctGrip = 0.33;      // share of grip a deflated tyre keeps
        public double punctDrop = 0.05;      // m, the corner drops on the rim
        public double punctRoll = 0.08;      // rolling drag of a flat tyre, as a share of a quarter of the weight
        public double punctKerbChance = 0.01; // per hard strike of a sausage kerb at speed
        public double punctHitChance = 0.25; // per heavy wheel hit (wall or car), scaled by the impact

        public CarConfig Clone()
        {
            var c = (CarConfig)MemberwiseClone();
            c.gearTopSpeeds = (double[])gearTopSpeeds.Clone();
            c.torqueRpm = (double[])torqueRpm.Clone(); c.torqueFrac = (double[])torqueFrac.Clone();
            return c;
        }

        // The same car as a fixed mass carrying `kg` of fuel, for the speed-profile solver (RacingLine): the fuel is
        // added to the mass, and mu is scaled so the tyres keep their load sensitivity about the reference car's
        // loads (the solver measures load against the car's own weight).
        public CarConfig WithFuel(double kg)
        {
            var c = Clone(); c.fuel = false; c.mass = mass + kg - fuelRef;
            c.mu = mu * Math.Pow(mass / c.mass, 1 - loadExp);
            return c;
        }

        // Car classes the player can choose. F1 = the 2026 car above. F2 = the FIA Formula 2 car the physics was
        // first calibrated on (620 hp, 795 kg with driver). F3 = FIA Formula 3 (3.4 L V6, ~380 hp, ~673 kg with driver);
        // F3 gearing and chassis figures are estimates.
        public static string[] ClassNames = { "Apex Premier", "Apex Challenger", "Apex Rookie" };
        public static CarConfig ForClass(int k)
        {
            // grip (mu) and downforce (clA) of F2 and F3 are fitted so that their gap to the F1 car at Monaco and Spa
            // matches the real gap between 2025 F2 / F3 poles and 2026 F1 poles (fit error 0.1 %)
            // F2 / F3 fuel flow: no flow limit; estimated from power at ~30 % efficiency (est.)
            // top speed: F2 620 hp (Mecachrome 3.4 V6 turbo), F3 380 hp (3.4 V6), both with DRS. FIA Monza 2025 maximum
            // speeds: F2 316 km/h in qualifying, 331 in the feature race (tow + DRS); F3 288 / 306. DRS (activeAero)
            // with these drag areas and a longer top gear gives ~325 / ~295 km/h flat out in still air.
            if (k == 1) { var c = LegacyF2(); c.activeAero = true; c.cdAX = 0.80; c.gearTopSpeeds = new double[] { 128, 168, 207, 245, 285, 335 }; c.chassis = true; c.brakeTemps = true; c.speedSens = 0.5; c.mu = 1.70; c.clA = 4.0; c.clAX = 3.4; c.fuel = true; c.fuelFlow = 120; c.tyreSurface = true; c.kerbTransfer = true; c.relaxLen = 0.33; c.downshiftKick = 0.3; return c; }
            if (k == 2) return new CarConfig
            {
                mass = 673, inertiaZ = 720, wheelbase = 2.95, frontWeight = 0.44, cgHeight = 0.29, wheelRadius = 0.30,
                mu = 1.65, wheelInertia = 1.2, track = 1.45, cdA = 0.90, clA = 2.8, activeAero = true, cdAX = 0.72, clAX = 2.5,
                rideF0 = 0.035, rideR0 = 0.055, springF = 180e3, springR = 160e3,
                peakTorque = 360, torqueRpm = new double[] { 3000, 5000, 6500, 7500, 8000, 8500 }, torqueFrac = new[] { 0.55, 0.82, 0.97, 1.0, 0.97, 0.88 },
                redline = 8500, idleRpm = 2500, gearTopSpeeds = new double[] { 115, 150, 182, 212, 248, 302 }, driveEff = 0.88,
                ersPower = 0, shiftTime = 0.05, brakeForce = 17000, brakeBias = 0.58, maxLockDeg = 18, speedSens = 0.5,
                fuelFlow = 75, sideCyA = 3.5, turboLag = 0, relaxLen = 0.30, clutchBite = 0, // naturally aspirated (no turbo hole to bridge)
            };
            return new CarConfig();
        }

        // The Formula 2 set-up the web prototype was built on. Only used by the regression tests,
        // which replay the JavaScript version's results.
        public static CarConfig LegacyF2() => new CarConfig
        {
            mass = 795, inertiaZ = 950, wheelbase = 3.135, frontWeight = 0.45, cgHeight = 0.28, wheelRadius = 0.33,
            chassis = false, brakeTemps = false, mu = 2.2, wheelInertia = 1.6, speedSens = 1.0, csFull = 0.14, track = 1.50, cdA = 1.05, clA = 4.0, activeAero = false, cdAX = 1.05, clAX = 4.0,
            peakTorque = 600, torqueRpm = new double[] { 2000, 4000, 5500, 6500, 7500, 8750 }, torqueFrac = new[] { 0.40, 0.62, 0.88, 1.0, 0.97, 0.84 },
            redline = 8750, idleRpm = 3000, gearTopSpeeds = new double[] { 128, 168, 207, 245, 283, 320 }, driveEff = 0.80,
            ersPower = 0, shiftTime = 0.05, brakeForce = 22500, brakeBias = 0.60, maxLockDeg = 18,
            fuel = false, tyreSurface = false, kerbTransfer = false, bbw = false, brakeFullGrip = false, frontSlipHeat = false, // the JS prototype had none of these (golden test)
            turboLag = 0, relaxLen = 0, downshiftKick = 0, clutchBite = 0,
        };
    }

    public struct CarInput
    {
        public double steer;    // -1..1, + = right
        public double throttle; // 0..1
        public double brake;    // 0..1
        public CarInput(double steer, double throttle, double brake) { this.steer = steer; this.throttle = throttle; this.brake = brake; }
    }

    // Damage to the car, 0 (fine) .. 1 (wrecked) per part. Wheels / corners: FL, FR, RL, RR (L = left).
    // Punctures: 0 = fine, otherwise the tyre deflates from just above 0 to 1 over about 1.5 s.
    public struct Damage
    {
        public double wingFL, wingFR, wingRear, floor;
        public double suspFL, suspFR, suspRL, suspRR;
        public double punctFL, punctFR, punctRL, punctRR;
        public int hits;                     // impacts recorded so far (the model throws debris for each new one)
        public int punctures, kerbPunctures; // tyres cut so far (all, and of those by kerbs)
        public double lastSize, lastX, lastY; // the last impact: 0..1 size, car-frame place (m ahead of the CG, m to the left)
        public double Susp(int w) => w == 0 ? suspFL : w == 1 ? suspFR : w == 2 ? suspRL : suspRR;
        public double Punct(int w) => w == 0 ? punctFL : w == 1 ? punctFR : w == 2 ? punctRL : punctRR;
        public void SetSusp(int w, double v) { v = v < 0 ? 0 : v > 1 ? 1 : v; if (w == 0) suspFL = v; else if (w == 1) suspFR = v; else if (w == 2) suspRL = v; else suspRR = v; }
        public void SetPunct(int w, double v) { if (w == 0) punctFL = v; else if (w == 1) punctFR = v; else if (w == 2) punctRL = v; else punctRR = v; }
        public bool Any => wingFL + wingFR + wingRear + floor + suspFL + suspFR + suspRL + suspRR + punctFL + punctFR + punctRL + punctRR > 0;
        public bool Punctured => punctFL + punctFR + punctRL + punctRR > 0;
        public bool WingsDamaged => wingFL + wingFR + wingRear > 0;
        // worth a stop for a new wing (or a flat tyre): the AI boxes at the next crossing of the line
        public bool NeedsPit => wingFL + wingFR > 0.9 || wingRear > 0.6 || Punctured;
    }

    public static class Surfaces
    {
        // peak friction relative to dry asphalt; kerbs use anti-slip paint
        public static readonly (double mu, double drag) Asphalt = (1.0, 0.0), Kerb = (0.95, 0.01), Grass = (0.2, 0.15); // slicks on grass: ~0.4 against ~2.2 on asphalt
    }

    public class FormulaCar
    {
        const double G = 9.81, RHO = 1.225;
        const double LowV = 3;              // m/s: below this the low-speed tyre and kinematic blends work (see Step)
        const double LowDamp = 0.04;        // s, tyre tread damping time at a standstill (about critical with the low-speed blend)
        public CarConfig cfg;

        // state
        public double x, y, heading, vx, vy, r;
        public double steerIn, delta, throttle, brake;
        public int gear = 1;
        public double rpm = 3000, shiftTimer, stopTimer;
        public bool reverse;
        public double axLong, ax, ay, ayF;
        public double fzF, fzR, useF, useR, alphaF, alphaR;
        public bool lockF, lockR, spinR, tcActive, absActive;
        // what each tyre is touching (0 FL, 1 FR, 2 RL, 3 RR): grip factor, rolling drag, bump height
        public readonly double[] wMu = { 1, 1, 1, 1 }, wRoll = new double[4], wBump = new double[4];
        public readonly SurfaceType[] wType = new SurfaceType[4];
        // m each tyre is lifted by a raised kerb (Track.KerbLift, set by WheelContact; 0 off the raised kerbs)
        public readonly double[] wLift = new double[4];
        public double vib;                  // 0..1 how hard the surface is shaking the car right now (camera, sound)
        public void SetWheelSurface(int w, SurfaceType type, double mu, double roll, double bump) { wType[w] = type; wMu[w] = mu; wRoll[w] = roll; wBump[w] = bump; wLift[w] = 0; }
        // one surface for all four tyres (older code and tests)
        public double surfaceMu { get => (wMu[0] + wMu[1] + wMu[2] + wMu[3]) / 4; set { for (int i = 0; i < 4; i++) wMu[i] = value; } }
        public double surfaceDrag { get => (wRoll[0] + wRoll[1] + wRoll[2] + wRoll[3]) / 4; set { for (int i = 0; i < 4; i++) wRoll[i] = value; } }
        // tyre wear 0 (new) .. 1 (finished), front and rear
        public double wearF, wearR;
        public double gripF = 1, gripR = 1;
        public double brakeTF = double.NaN, brakeTR;           // deg C, front and rear brakes
        // friction of carbon brakes vs temperature: ~55% at room temperature, full from ~400 C, fading above 1000 C
        public static double BrakeMu(double t)
        {
            double cold = Math.Min(1, Math.Max(0, (t - 120) / 280)); cold = cold * cold * (3 - 2 * cold);
            double hot = Math.Min(1, Math.Max(0, (t - 1000) / 250)); hot = hot * hot * (3 - 2 * hot);
            return (0.55 + 0.45 * cold) * (1 - 0.3 * hot);
        }  // tyre grip now from temperature, compound and wear (1 = a fresh Soft at its best)
        // chassis detail: state and read-outs
        public double tow;                  // 0..1 how deep in another car's slipstream (set by the game)
        public double rideF = double.NaN, rideR, bodyRoll, pitch;      // m, m, rad, rad
        public double tyreTF = double.NaN, tyreTR;                 // deg C, front and rear tyres
        public double aeroBalance, downforce, dragForce;           // front share now, N, N
        public bool bottoming;
        public double floorHit;             // 0..1 how hard the plank is on the road now (downforce lost, see CarConfig.floorLoss)
        public double grade;                // slope of the road in the direction the car points (rise per metre, + = uphill)
        public double bank;                 // rad, road banking across the car (+ = the road rises to the car's right)
        public double wheelSpin;
        public double lockV = double.NaN, csBlend, lockAngle;
        public double wR = double.NaN;      // rear wheel spin speed (rad/s)
        public double slipRatioR, Fdrive, FxR, DR, torque;
        public double aeroX;                // 0 = wings closed (corner mode), 1 = open (straight mode)
        public double ersEnergy = double.NaN, ersHarvested, ersKw; // battery (J), harvested this lap (J), MGU-K power now (kW, - = charging)
        // fuel (kg) left, used this lap / last lap, and the car's mass now (kg)
        public double fuel = double.NaN, fuelThisLap, fuelLastLap = double.NaN, massNow;
        // tyre surface temperature (deg C) and pressure (psi), front and rear; dirt picked up (0..1) front and rear
        public double tyreSF = double.NaN, tyreSR, tyrePF, tyrePR, dirtF, dirtR;
        public double biasNow;              // front share of all braking force right now (brakes + engine + MGU-K)
        public double windX, windY;         // m/s, world frame: the way the air is moving (set by the game per session)
        public double boost = double.NaN;   // 0..1 turbo boost now
        double relF = double.NaN, relR, kick; // relaxed slip angles (rad), downshift impulse left (N s)
        public bool standingStart;          // pulling away from rest: no MGU-K drive until ersStartKmh (FIA 2026)
        public double ersStart = 1;         // 0..1 MGU-K drive let in after a standing start (ramps over cfg.ersStartRamp)
        // held on the grid before the start (set by the game every step until the car may go): holding the brake at
        // rest does not select reverse (it did after 0.6 s, so every car reversed off its slot before lights out)
        public bool gridHold;
        public double deployMap = 1;        // -1..2 deploy map here (set by the game / AI from RacingLine.deploy; see CarConfig.ersMapSoc)
        // Overtake Mode and boost (CarConfig.otEnergy). otArmed: set by the game (OvertakeRule); otOn, boostOn: the driver's
        // buttons (player or AI); otActive / boostActive: in use this step (read-outs). otEnergy: overtake energy left this
        // lap (J, NaN = full); the new lap's allowance is given at the next detection point (otRefill), so a section that
        // runs across the line does not get two.
        public bool otArmed, otOn, boostOn, otActive, boostActive, otRefill;
        public double otEnergy = double.NaN;
        public int otIdx = -1;              // track point at OvertakeRule's last look at this car
        public double OtLeft => double.IsNaN(otEnergy) ? cfg.otEnergy : otEnergy;
        public double OtFrac => cfg.otEnergy > 0 ? Clamp(OtLeft / cfg.otEnergy, 0, 1) : 0;
        public void OtRefill() { otEnergy = cfg.otEnergy; otRefill = false; }
        public void NewLap() { ersHarvested = 0; fuelLastLap = fuelThisLap; fuelThisLap = 0; otRefill = true; }

        // ---------- damage ----------
        public Damage damage;               // always recorded; changes the physics only with cfg.damage
        readonly double[] dmgMu = { 1, 1, 1, 1 };   // per-wheel grip factor from damage (filled in Step)
        readonly SurfaceType[] dmgPrevType = new SurfaceType[4];
        uint dmgRng = 0x9E3779B9u;          // deterministic dice for punctures (same run, same result)
        public void SeedDamage(int seed) { dmgRng = (uint)seed * 2654435761u + 0x9E3779B9u; if (dmgRng == 0) dmgRng = 1; }
        double Dice() { dmgRng ^= dmgRng << 13; dmgRng ^= dmgRng >> 17; dmgRng ^= dmgRng << 5; return dmgRng / 4294967296.0; }
        static double Sat(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // A hit: closing speed (m/s, along the contact normal) and where on the car it landed (car frame: lx m ahead of
        // the CG, ly m to the left). Called by Traffic.Collide and Traffic.Wall.
        public void ApplyImpact(double relSpeed, double lx, double ly, bool wall = false)
        {
            var c = cfg;
            double e = (relSpeed - c.dmgMinSpeed) / c.dmgSpeed;
            if (e <= 0) return;
            if (wall) e *= 1.3;             // a wall does not give like another car's bodywork
            e = Math.Min(1.5, e);
            double aF = c.wheelbase * (1 - c.frontWeight), bR = c.wheelbase * c.frontWeight;
            bool left = ly > 0, side = Math.Abs(ly) > 0.55;
            int wF = left ? 0 : 1, wRr = left ? 2 : 3;
            ref Damage d = ref damage;
            if (lx > aF + 0.15)
            {
                // ahead of the front wheels: the front wing (both sides when it is hit square on)
                if (!side) { d.wingFL = Sat(d.wingFL + 0.75 * e); d.wingFR = Sat(d.wingFR + 0.75 * e); }
                else if (left) { d.wingFL = Sat(d.wingFL + e); d.wingFR = Sat(d.wingFR + 0.15 * e); }
                else { d.wingFR = Sat(d.wingFR + e); d.wingFL = Sat(d.wingFL + 0.15 * e); }
                if (side) d.SetSusp(wF, d.Susp(wF) + 0.25 * e);
            }
            else if (lx > 0)
            {
                // beside the front wheel: the wheel takes it (wishbones, track rod), the endplate goes too
                if (side) { d.SetSusp(wF, d.Susp(wF) + 0.8 * e); if (left) d.wingFL = Sat(d.wingFL + 0.4 * e); else d.wingFR = Sat(d.wingFR + 0.4 * e); }
                d.floor = Sat(d.floor + 0.15 * e);
            }
            else if (lx > -bR - 0.15)
            {
                if (side) d.SetSusp(wRr, d.Susp(wRr) + 0.7 * e);
                d.floor = Sat(d.floor + 0.25 * e);
            }
            else
            {
                // from behind: rear wing, diffuser
                d.wingRear = Sat(d.wingRear + 0.45 * e); d.floor = Sat(d.floor + 0.15 * e);
                if (side) d.SetSusp(wRr, d.Susp(wRr) + 0.35 * e);
            }
            // a heavy hit on a wheel can cut the tyre
            if (side && relSpeed > 5)
            {
                int w = lx > 0 ? wF : wRr;
                if (d.Punct(w) == 0 && Dice() < c.punctHitChance * Math.Min(1, (relSpeed - 5) / 10)) { d.SetPunct(w, 1e-3); d.punctures++; }
            }
            d.hits++; d.lastSize = Math.Min(1, e); d.lastX = lx; d.lastY = ly;
        }

        // the pit crew fits a new nose / rear wing (suspension and floor damage stay)
        public void RepairWings() { damage.wingFL = damage.wingFR = damage.wingRear = 0; }
        // new tyres: no more punctures
        public void FixPunctures() { damage.punctFL = damage.punctFR = damage.punctRL = damage.punctRR = 0; }
        public void ClearDamage() { int h = damage.hits; damage = default; damage.hits = h; }   // (hits kept: the model compares it)

        // How much of the undamaged car's cornering grip is left at speed v (0..1, the weaker axle). For the AI's
        // speed profile; 1 when damage physics is off.
        public double DamageGrip(double v)
        {
            if (!cfg.damage || !damage.Any) return 1;
            var c = cfg; ref Damage d = ref damage;
            double W = c.mass * G, D = 0.5 * RHO * c.clA * v * v;
            double wf = W * c.frontWeight, wr = W - wf, df = D * c.aeroFront, dr = D - df;
            double fl = Sat(c.dmgFrontWing * (d.wingFL + d.wingFR)), rl = Sat(c.dmgRearWing * d.wingRear), kl = Sat(c.dmgFloor * d.floor);
            double gF = (WheelDmgMu(0) + WheelDmgMu(1)) / 2, gR = (WheelDmgMu(2) + WheelDmgMu(3)) / 2;
            double kF = Math.Pow((wf + df * (1 - fl) * (1 - kl)) / (wf + df), c.loadExp) * gF;
            double kR = Math.Pow((wr + dr * (1 - rl) * (1 - kl)) / (wr + dr), c.loadExp) * gR;
            return Math.Min(kF, kR);
        }
        double WheelDmgMu(int w)
        {
            double p = damage.Punct(w);
            return (1 - cfg.dmgSuspGrip * damage.Susp(w)) * (p > 0 ? 1 - (1 - cfg.punctGrip) * Math.Min(1, p) : 1);
        }
        // toe of each axle from bent suspension (rad, + = wheels point left): a damaged wheel turns out on its side
        double FrontToe() => cfg.dmgToe * Math.PI / 180 * (damage.suspFL - damage.suspFR) / 2;
        double RearToe() => cfg.dmgToe * cfg.dmgRearToe * Math.PI / 180 * (damage.suspRL - damage.suspRR) / 2;
        public double SteerOffsetDeg => cfg.dmgSteer * (damage.suspFL - damage.suspFR) + FrontToe() * 180 / Math.PI;

        // per step, damage physics only: tyres deflating, kerb strikes, per-wheel grip
        void DamageStep(double dt, double speed)
        {
            var c = cfg; ref Damage d = ref damage;
            for (int w = 0; w < 4; w++)
            {
                double p = d.Punct(w);
                if (p > 0 && p < 1) d.SetPunct(w, Math.Min(1, p + dt / 1.5));
                // hitting a sausage kerb hard: floor damage now and then, and a small chance of a cut tyre
                if (wType[w] == SurfaceType.SausageKerb && dmgPrevType[w] != SurfaceType.SausageKerb && speed > 25)
                {
                    d.floor = Sat(d.floor + 0.03 * Math.Min(2, speed / 50));
                    if (d.Punct(w) == 0 && Dice() < c.punctKerbChance * Math.Min(2, speed / 50)) { d.SetPunct(w, 1e-3); d.punctures++; d.kerbPunctures++; }
                }
                dmgPrevType[w] = wType[w];
                dmgMu[w] = WheelDmgMu(w);
            }
        }

        // combustion engine's peak power from the torque curve (cached), for the fuel flow
        double icePeakKey1 = double.NaN, icePeakKey2 = double.NaN, icePeak;
        double IcePeak(CarConfig c)
        {
            if (c.peakTorque != icePeakKey1 || c.redline != icePeakKey2)
            {
                double p = 1;
                for (double rr = c.idleRpm; rr <= c.redline; rr += 50) p = Math.Max(p, TorqueAt(c, rr) * rr * Math.PI / 30);
                icePeakKey1 = c.peakTorque; icePeakKey2 = c.redline; icePeak = p;
            }
            return icePeak;
        }

        public FormulaCar(CarConfig cfg = null) { this.cfg = cfg ?? new CarConfig(); }

        // how much a surface dirties the tyres (fully dirty after about 30 m on it)
        static double Dirty(SurfaceType t) => t == SurfaceType.Grass || t == SurfaceType.Gravel ? 1 : t == SurfaceType.Astroturf ? 0.5 : 0;
        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;
        static double Approach(double v, double t, double d) => v < t ? Math.Min(v + d, t) : Math.Max(v - d, t);
        static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        public static double GearRatio(CarConfig c, int gear) =>
            (c.redline * 2 * Math.PI / 60 * c.wheelRadius) / (c.gearTopSpeeds[gear - 1] / 3.6);

        public static double TorqueAt(CarConfig c, double rpm)
        {
            double[] R = c.torqueRpm, F = c.torqueFrac; int n = R.Length;
            if (rpm <= R[0]) return c.peakTorque * F[0];
            for (int i = 1; i < n; i++)
                if (rpm <= R[i])
                {
                    double r0 = R[i - 1], f0 = F[i - 1], r1 = R[i], f1 = F[i];
                    return c.peakTorque * (f0 + (f1 - f0) * (rpm - r0) / (r1 - r0));
                }
            return c.peakTorque * F[n - 1];
        }

        // MGU-K power allowed at this road speed (FIA 2026 ramp-down): full to 290 km/h,
        // 1800 - 5 v [kW] to 340 km/h (100 kW), then down to zero at 345 km/h
        public static double ErsPowerAt(CarConfig c, double v, bool fade = true)
        {
            double kmh = Math.Abs(v) * 3.6, f = kmh <= 290 ? 1 : kmh <= 340 ? (1800 - 5 * kmh) / 350 : kmh < 345 ? (345 - kmh) / 5 * (100.0 / 350) : 0;
            if (fade && kmh > c.ersFadeFrom) f = Math.Min(f, 1 - (1 - c.ersFadeFrac) * Math.Min(1, (kmh - c.ersFadeFrom) / (c.ersFadeTo - c.ersFadeFrom)));
            return c.ersPower * f;
        }

        // MGU-K power allowed in Overtake Mode: full to otFullKmh (337 km/h), then linearly down to zero at otZeroKmh
        public static double OvertakePowerAt(CarConfig c, double v)
        {
            double kmh = Math.Abs(v) * 3.6;
            if (kmh <= c.otFullKmh) return c.ersPower;
            if (kmh >= c.otZeroKmh) return 0;
            return c.ersPower * (c.otZeroKmh - kmh) / (c.otZeroKmh - c.otFullKmh);
        }

        // full-throttle MGU-K power for this speed and state of charge (0..1): + = deploy, - = superclip harvest (W)
        public static double ErsFull(CarConfig c, double v, double soc, double map = 1)
        {
            if (!c.ersManage) return ErsPowerAt(c, v);
            double kmh = Math.Abs(v) * 3.6, vClip = c.ersClipKmh + c.ersClipSoc * soc;
            // the map's superclip zone (the last seconds of a long straight), unless the battery is nearly full
            // (full below 30 % charge, none above 60 %: a car with charge to spare keeps its speed)
            if (map < 0 && kmh > c.ersClipMinKmh) return -c.ersClipPower * Math.Min(1, -map) * Clamp((0.6 - soc) / 0.3, 0, 1);
            if (kmh > vClip) return -c.ersClipPower * Math.Min(1, (kmh - vClip) / c.ersClipRamp);
            if (map < 0) map = 0;
            double gate = Clamp((c.ersDepKmh + c.ersDepSoc * soc - kmh) / c.ersDepRamp, 0, 1);
            if (map > 1)
            {
                // a long straight ahead: lift the fade towards the FIA ramp-down (full 350 kW to 290 km/h)
                double fia = ErsPowerAt(c, v, false), fade = ErsPowerAt(c, v);
                return (fade + (fia - fade) * Math.Min(1, map - 1)) * gate;
            }
            map += (1 - map) * Clamp((soc - c.ersMapSoc) / (1 - c.ersMapSoc), 0, 1);
            return ErsPowerAt(c, v) * map * gate;
        }

        // slip (in B*alpha units) where the magic formula peaks, for this C/E shape (cached)
        double peakKeyC = double.NaN, peakKeyE = double.NaN, peakCached;
        double PeakBa(CarConfig c)
        {
            if (c.tireC != peakKeyC || c.tireE != peakKeyE)
            {
                double best = 0, v = 1;
                for (double xx = 0.05; xx < 6; xx += 0.002)
                {
                    double f = Math.Sin(c.tireC * Math.Atan(xx - c.tireE * (xx - Math.Atan(xx))));
                    if (f > best) { best = f; v = xx; }
                }
                peakKeyC = c.tireC; peakKeyE = c.tireE; peakCached = v;
            }
            return peakCached;
        }

        public static double Magic(CarConfig c, double alpha, double loadRatio = 1)
        {
            double Ba = c.tireB * Math.Pow(loadRatio, -c.slipGrowth) * alpha;
            return Math.Sin(c.tireC * Math.Atan(Ba - c.tireE * (Ba - Math.Atan(Ba))));
        }

        public void Shift(int dir)
        {
            int g = (int)Clamp(gear + dir, 1, cfg.gearTopSpeeds.Length);
            if (g != gear)
            {
                if (g < gear && cfg.downshiftKick > 0 && vx > 1)
                    kick += cfg.downshiftKick * cfg.engineInertia * (vx / cfg.wheelRadius) * (GearRatio(cfg, g) - GearRatio(cfg, gear)) * GearRatio(cfg, g) / cfg.wheelRadius;
                gear = g; shiftTimer = cfg.shiftTime;
            }
        }

        struct Combined { public double Fx, Fy, use; public bool slide, limited; }

        // front axle: longitudinal demand Fx combined with lateral Fy inside the friction circle D
        static Combined Combine(double Fx, double Fy, double D, bool limiter, double latPriority = 0.75)
        {
            bool limited = false;
            double lat = Math.Min(Math.Abs(Fy), D);
            if (limiter)
            {
                double floor = Fx > 0 ? 0.05 : 0.25;
                double maxFx = Math.Max(floor * D, Math.Sqrt(Math.Max(0, D * D - (latPriority * lat) * (latPriority * lat))) * 0.95);
                if (Math.Abs(Fx) > maxFx) { Fx = Math.Sign(Fx) * maxFx; limited = true; }
            }
            else
            {
                double lockAt = Math.Sqrt(Math.Max(0, D * D - (0.6 * lat) * (0.6 * lat))) * 0.98;
                if (Math.Abs(Fx) > lockAt)
                {
                    double k = Math.Min(1, (Math.Abs(Fx) - lockAt) / (0.6 * D));
                    double sk = k * k * (3 - 2 * k);
                    double fxOut = lockAt + (0.85 * D - lockAt) * sk;
                    double fyCap = Math.Sqrt(Math.Max(0, D * D - fxOut * fxOut)) * (1 - sk) + 0.12 * D * sk;
                    return new Combined { Fx = Math.Sign(Fx) * fxOut, Fy = Clamp(Fy, -fyCap, fyCap), slide = sk > 0.5, limited = limited, use = 1 + 0.1 * sk };
                }
            }
            double fyMax = Math.Sqrt(Math.Max(0, D * D - Fx * Fx));
            double use = Hypot(Fx, Fy) / D;
            Fy = Clamp(Fy, -fyMax, fyMax);
            return new Combined { Fx = Fx, Fy = Fy, slide = false, limited = limited, use = use };
        }

        struct RearForce { public double Fx, Fy, s, kap; }

        public void Step(CarInput input, double dt)
        {
            var c = cfg;
            double m = c.mass, Iz = c.inertiaZ, fw = c.frontWeight, hcg = c.cgHeight;
            if (c.fuel)
            {
                // the fuel is a point mass at the tank: the car's mass, weight split, CG height and yaw inertia follow it
                if (double.IsNaN(fuel)) fuel = c.fuelStart;
                double dm = fuel - c.fuelRef;
                if (dm != 0)
                {
                    double m1 = m + dm, dx = c.wheelbase * (c.fuelFront - fw); // tank position relative to the reference CG
                    fw = (m * fw + dm * c.fuelFront) / m1;
                    hcg = (m * hcg + dm * c.fuelHeight) / m1;
                    // parallel axes: both bodies about the new CG
                    double s = dx * dm / m1;
                    Iz = Iz + m * s * s + dm * (c.fuelGyr * c.fuelGyr + (dx - s) * (dx - s));
                    m = m1;
                }
            }
            massNow = m;
            double L = c.wheelbase, b = L * fw, a = L - b; // a: CG->front, b: CG->rear
            double speed = Hypot(vx, vy);

            // ---------- steering filter ----------
            double target = Clamp(input.steer, -1, 1);
            double beta = Math.Atan2(vy, Math.Max(Math.Abs(vx), 1));
            bool countering = -target * beta > 0 && Math.Abs(beta) > 0.03;
            bool unwinding = Math.Abs(target) < Math.Abs(steerIn) || Math.Sign(target) != Math.Sign(steerIn);
            double rate = countering ? Math.Max(c.steerReturn, c.steerSpeed * 2) : unwinding ? c.steerReturn : c.steerSpeed;
            steerIn = Approach(steerIn, target, rate * dt);
            double fullLock = c.maxLockDeg * Math.PI / 180;
            // lock follows speed when turning in or straightening, but is not reduced while holding a steady input
            if (double.IsNaN(lockV) || speed < lockV) lockV = speed;
            else if (!countering && (Math.Abs(target) > Math.Abs(steerIn) + 0.02 || Math.Abs(steerIn) < 0.1)) lockV = Math.Min(speed, lockV + 10 * dt);
            double speedLock = fullLock / (1 + c.speedSens * (lockV / 30) * (lockV / 30));
            double cs = countering && c.csOpen ? Clamp(Math.Abs(beta) / c.csFull, 0, 1) : 0;
            csBlend = csBlend + (cs - csBlend) * Math.Min(1, dt * 12);
            double lockA = speedLock + (fullLock - speedLock) * csBlend;
            lockAngle = lockA;
            delta = -steerIn * lockA; // + delta = left
            if (c.damage) { DamageStep(dt, speed); delta += SteerOffsetDeg * Math.PI / 180; } // bent steering: the wheels point off-centre

            throttle = Clamp(input.throttle, 0, 1);
            brake = Clamp(input.brake, 0, 1);
            double thr = throttle, brk = brake;
            // brake balance (front share); migration moves it rearwards as the pedal is eased
            double bias = c.brakeMigration != 0 ? Clamp(c.brakeBias - c.brakeMigration * (1 - brk), 0.3, 0.8) : c.brakeBias;
            if (c.bbw) bias = Math.Min(0.8, bias + c.bbwShift * Clamp(1 - Math.Abs(vx) * 3.6 / c.bbwShiftKmh, 0, 1));

            // ---------- reverse: hold brake when stopped ----------
            if (gridHold) { reverse = false; stopTimer = 0; }
            else if (!reverse)
            {
                if (speed < 0.5 && brk > 0.1 && thr < 0.05) { stopTimer += dt; if (stopTimer > 0.6) { reverse = true; gear = 1; } }
                else stopTimer = 0;
            }
            else if (thr > 0.1) { reverse = false; stopTimer = 0; }

            // ---------- aero + loads ----------
            double q = 0.5 * RHO * vx * vx, betaAero = beta, windSide = 0;
            bool windy = windX != 0 || windY != 0;
            if (windy)
            {
                // the car meets the air at its speed relative to the air: a headwind adds drag and downforce,
                // a tailwind takes them away; a crosswind turns the airflow (aero yaw) and pushes on the car's side
                double chw = Math.Cos(heading), shw = Math.Sin(heading);
                double ux = vx - (windX * chw + windY * shw), uy = vy - (-windX * shw + windY * chw);
                q = 0.5 * RHO * ux * ux;
                betaAero = Math.Atan2(uy, Math.Max(Math.Abs(ux), 1));
                // side force from the wind only (the car's own sideslip is already in the tyre calibration)
                windSide = -0.5 * RHO * c.sideCyA * (Hypot(ux, uy) * uy - Hypot(vx, vy) * vy);
            }
            if (c.activeAero)
            {
                // wings open on the straights: full throttle, no brake, little cornering; they close at once otherwise
                bool open = thr > 0.9 && brk <= 0 && vx > 30 && Math.Abs(ayF) < (aeroX > 0.5 ? 9 : 6);
                aeroX = Approach(aeroX, open ? 1 : 0, (open ? 4 : 8) * dt);
            }
            double down = q * (c.clA + (c.clAX - c.clA) * aeroX), drag = q * (c.cdA + (c.cdAX - c.cdA) * aeroX);
            double aeroF = c.aeroFront, tempKF = 1, tempKR = 1;
            if (c.chassis)
            {
                if (double.IsNaN(rideF)) { rideF = c.rideF0; rideR = c.rideR0; }
                if (double.IsNaN(tyreTF)) { tyreTF = c.tyreStart; tyreTR = c.tyreStart; }
                // ground effect: the lower the floor runs, the more downforce, until it gets so low that it stalls
                double hm = (rideF + rideR) / 2;
                double downK = Clamp(1 + c.rideGain * (c.rideDesign - hm) / c.rideDesign, 0.8, 1.12) * Clamp(hm / c.rideStall, 0.6, 1);
                downK *= 1 - Math.Min(0.15, c.yawSens * Math.Abs(betaAero)); // sliding sideways (or a crosswind) spoils the airflow
                downK *= 1 - c.towDown * tow;                               // dirty air behind another car
                down *= downK; drag *= 1 - c.towDrag * tow;
                if (floorHit > 0) { double lost = down * c.floorLoss * floorHit; down -= lost; drag += c.plankMu * lost * (vx > 1 ? 1 : 0); }
                // nose down (braking) moves the aero balance forward; squatting (acceleration) moves it back
                aeroF = c.aeroFront + Clamp(c.rakeGain * ((rideR - rideF) - (c.rideR0 - c.rideF0)), -0.06, 0.06);
                // tyres give their best grip in a temperature window
                var cmp = CompoundTable.Get(c.compound);
                double gTF = tyreTF, gTR = tyreTR;
                if (c.tyreSurface)
                {
                    if (double.IsNaN(tyreSF)) { tyreSF = tyreTF; tyreSR = tyreTR; }
                    gTF += c.tyreSurfGrip * (tyreSF - tyreTF); gTR += c.tyreSurfGrip * (tyreSR - tyreTR);
                }
                double eF = (gTF - cmp.opt) / cmp.window, eR = (gTR - cmp.opt) / cmp.window;
                tempKF = Math.Max(0.75, 1 - c.tyreLoss * eF * eF); tempKR = Math.Max(0.75, 1 - c.tyreLoss * eR * eR);
                // dirt picked up off the track
                if (dirtF > 0) tempKF *= 1 - c.dirtLoss * dirtF;
                if (dirtR > 0) tempKR *= 1 - c.dirtLoss * dirtR;
                // carcass temperature sets the pressure (gas law, from the set pressure at blanket temperature)
                // (absolute pressure scales with absolute temperature; 14.7 psi of atmosphere)
                tyrePF = (c.tyrePressF + 14.7) * (tyreTF + 273.15) / (c.tyreStart + 273.15) - 14.7;
                tyrePR = (c.tyrePressR + 14.7) * (tyreTR + 273.15) / (c.tyreStart + 273.15) - 14.7;
                // compound grip, and worn tyres grip less (slowly at first, then they fall off a cliff)
                tempKF *= cmp.grip * (1 - 0.05 * wearF - 0.12 * Math.Pow(wearF, 4));
                tempKR *= cmp.grip * (1 - 0.05 * wearR - 0.12 * Math.Pow(wearR, 4));
                // toe: the wheels are set a fraction of a degree off straight, which scrubs a little
                double tf = c.toeF * Math.PI / 180, tr2 = c.toeR * Math.PI / 180;
                drag += (tf * tf + tr2 * tr2) * 2 * 250e3 * (vx > 1 ? 1 : 0);
            }
            if (c.damage && damage.Any)
            {
                // damaged aero: the front wing carries part of the front's share, the rear wing part of the rear's, the
                // floor some of everything. Front wing damage moves the balance rearwards (understeer).
                ref Damage dg = ref damage;
                double dF = down * aeroF * (1 - Sat(c.dmgFrontWing * (dg.wingFL + dg.wingFR))), dR = down * (1 - aeroF) * (1 - Sat(c.dmgRearWing * dg.wingRear));
                double kFl = 1 - Sat(c.dmgFloor * dg.floor);
                dF *= kFl; dR *= kFl;
                down = dF + dR; aeroF = down > 1 ? dF / down : aeroF;
                drag *= 1 - c.dmgRearDrag * dg.wingRear + c.dmgFrontDrag * (dg.wingFL + dg.wingFR);
                // bent toe scrubs the tyres
                double tf2 = FrontToe(), tr3 = RearToe();
                drag += (tf2 * tf2 + tr3 * tr3) * 2 * 250e3 * (vx > 1 ? 1 : 0);
            }
            aeroBalance = aeroF; downforce = down; dragForce = drag; gripF = tempKF; gripR = tempKR;
            double transfer = m * axLong * hcg / L;
            double fz0F = m * G * b / L, fz0R = m * G * a / L;
            // tyre nominal loads and static ride heights belong to the reference car (with fuelRef kg on board), so
            // extra fuel sinks the car and meets the tyres' load sensitivity instead of getting grip for free
            double nF0 = fz0F, nR0 = fz0R;
            if (m != c.mass) { double rb = L * c.frontWeight; nF0 = c.mass * G * rb / L; nR0 = c.mass * G * (L - rb) / L; }
            // banking: the road takes part of the weight and of the cornering load as extra normal force
            double bankN = bank == 0 ? 0 : m * G * (Math.Cos(bank) - 1) + m * ayF * Math.Sin(bank);
            fzF = Math.Max(300, fz0F - transfer + down * aeroF + bankN * b / L);
            fzR = Math.Max(300, fz0R + transfer + down * (1 - aeroF) + bankN * a / L);
            ayF = ayF + (ay - ayF) * Math.Min(1, dt * 10);
            double dLat = m * Math.Abs(ayF) * hcg / c.track;
            if (c.chassis)
            {
                // suspension: each axle sinks under load (downforce, braking, acceleration) and settles with a short lag
                double k = Math.Min(1, dt * c.heaveRate);
                double tgF = c.rideF0 - (fzF - nF0) / c.springF, tgR = c.rideR0 - (fzR - nR0) / c.springR;
                if (c.damage && damage.Punctured)
                {
                    // a flat tyre: that corner sits on the rim, the floor drops (and strikes the road sooner)
                    tgF -= c.punctDrop * 0.5 * (Math.Min(1, damage.punctFL) + Math.Min(1, damage.punctFR));
                    tgR -= c.punctDrop * 0.5 * (Math.Min(1, damage.punctRL) + Math.Min(1, damage.punctRR));
                }
                bottoming = tgF < 0.003 || tgR < 0.003;
                // how hard: 0.3 at a touch, 1 at 4 mm or more below the plank's limit; follows within ~0.05 s
                double hit = bottoming ? Clamp((0.003 - Math.Min(tgF, tgR)) / 0.004, 0.3, 1) : 0;
                floorHit += (hit - floorHit) * Math.Min(1, dt * 20);
                rideF += (Math.Max(0.003, tgF) - rideF) * k; rideR += (Math.Max(0.003, tgR) - rideR) * k;
                bodyRoll = m * ayF * hcg / c.rollStiff; pitch = ((rideR - rideF) - (c.rideR0 - c.rideF0)) / L;
            }
            double wLat = Clamp(Math.Abs(ayF) / 10, 0, 1), rollDeg = Math.Abs(bodyRoll) * 180 / Math.PI * 0.7;
            // bumps (kerb ridges, ruts) make the tyre load jump about; a loaded tyre then grips less on average
            double vk = Math.Min(1, speed / 15);
            vib = 0;
            for (int w = 0; w < 4; w++) vib = Math.Max(vib, wBump[w] / 0.012 * vk);
            vib = Math.Min(1, vib);
            double Wm(int w) => (c.damage ? wMu[w] * dmgMu[w] : wMu[w]) * (1 - Math.Min(0.3, wBump[w] * 4.5 * vk));
            // in a left turn (ayF > 0) the right-hand tyres are the outer, loaded ones
            bool rightOuter = ayF >= 0;
            // kerb on one side: the tyres there ride higher, the roll springs push load onto them (dk = load moved to the
            // right-hand tyre, N). Axle roll stiffness K moves K * (height difference / track) / track.
            double dkF = 0, dkR = 0;
            if (c.kerbTransfer)
            {
                double kt = c.kerbLift * c.rollStiff / (c.track * c.track);
                dkF = (wBump[1] - wBump[0]) * kt * c.latTransferFront;
                dkR = (wBump[3] - wBump[2]) * kt * (1 - c.latTransferFront);
                if (wLift[0] + wLift[1] + wLift[2] + wLift[3] > 0)
                {
                    // a raised kerb under one side: that side rides up by its slope (only there: the flat kerbs and the
                    // road are untouched), and the car shakes harder on it
                    double kr = c.kerbRaise * c.rollStiff / (c.track * c.track);
                    dkF += (wLift[1] - wLift[0]) * kr * c.latTransferFront;
                    dkR += (wLift[3] - wLift[2]) * kr * (1 - c.latTransferFront);
                    for (int w = 0; w < 4; w++) vib = Math.Max(vib, Math.Min(1, wLift[w] / 0.03 * vk));
                }
            }
            double AxleGrip(double fz, double fz0, double share, double camber, double muL, double muR, double dk)
            {
                double half = fz / 2, n = fz0 / 2, d = Math.Min(half * 0.95, dLat * share);
                double mo = rightOuter ? muR : muL, mi = rightOuter ? muL : muR;
                if (c.chassis)
                {
                    // camber: the wheels lean in at the top. In a corner that suits the loaded outer tyre and hurts
                    // the inner one; in a straight line it costs a little braking and traction.
                    double lean = Math.Abs(camber), straight = 1 - 0.004 * lean;
                    double po = lean - rollDeg, pi = -lean - rollDeg;
                    mo *= straight + (1 + 0.012 * po - 0.0016 * po * po - straight) * wLat;
                    mi *= straight + (1 + 0.012 * pi - 0.0016 * pi * pi - straight) * wLat;
                }
                if (dk != 0)
                {
                    // signed transfer to the right-hand tyre: cornering plus kerb, at most 95 % of the axle's half load
                    double sR = Clamp((rightOuter ? d : -d) + dk, -half * 0.95, half * 0.95);
                    double mR = rightOuter ? mo : mi, mL = rightOuter ? mi : mo;
                    return n * (Math.Pow((half + sR) / n, c.loadExp) * mR + Math.Pow((half - sR) / n, c.loadExp) * mL);
                }
                return n * (Math.Pow((half + d) / n, c.loadExp) * mo + Math.Pow((half - d) / n, c.loadExp) * mi);
            }
            double DF = c.mu * tempKF * AxleGrip(fzF, nF0, c.latTransferFront, c.camberF, Wm(0), Wm(1), dkF);
            double DRl = c.mu * c.rearGrip * tempKR * AxleGrip(fzR, nR0, 1 - c.latTransferFront, c.camberR, Wm(2), Wm(3), dkR);

            // ---------- engine / drive ----------
            double ratio = GearRatio(c, gear);
            if (double.IsNaN(wR)) wR = vx / c.wheelRadius;
            double wheelRpm = Math.Abs(wR) * 60 / (2 * Math.PI);
            double rpmT = wheelRpm * ratio;
            double engage = Clamp(1 - Math.Abs(vx) / c.clutchEngage, 0, 1);
            double launch = c.idleRpm + thr * (0.5 * c.redline * engage + Math.Max(0, c.clutchBite - c.idleRpm) * (1 - engage));
            if (gear == 1 && rpmT < launch) rpmT = launch;
            rpmT = Math.Max(rpmT, c.idleRpm);
            rpm += (Math.Min(rpmT, c.redline + 200) - rpm) * Math.Min(1, dt * 30);

            // auto gearbox shifts on road speed, so wheelspin hits the limiter instead of shifting up
            double roadRpm = Math.Abs(vx) / c.wheelRadius * 60 / (2 * Math.PI);
            if (c.autoShift && !reverse && shiftTimer <= 0)
            {
                if (roadRpm * ratio > c.redline * 0.975 && gear < c.gearTopSpeeds.Length) Shift(1);
                else if (gear > 1)
                {
                    double rpmLower = roadRpm * GearRatio(c, gear - 1);
                    if (rpmLower < c.redline * 0.86 && roadRpm * ratio < c.redline * 0.68) Shift(-1);
                }
            }
            shiftTimer = Math.Max(0, shiftTimer - dt);

            double fdrive = 0, pIce = 0, ebF = 0;
            bool dry = c.fuel && fuel <= 0;   // out of fuel: the combustion engine stops pushing
            if (reverse)
            {
                if (vx > -2.8) fdrive = -brk * 3500;
                brk = 0;
            }
            else if (shiftTimer <= 0)
            {
                double kBoost = 1;
                if (c.turboLag > 0)
                {
                    if (double.IsNaN(boost)) boost = 1;                   // a fresh car is on the grid with the turbo spooled
                    double want = speed < 0.5 ? 1 : thr;                   // (the driver holds the revs before the start)
                    boost += (want - boost) * Math.Min(1, dt / (want > boost ? c.turboLag : 3 * c.turboLag));
                    kBoost = c.turboNa + (1 - c.turboNa) * boost;
                }
                if (rpm < c.redline && !dry) { double te = TorqueAt(c, rpm) * thr * kBoost; fdrive = te * ratio * c.driveEff / c.wheelRadius; pIce = te * rpm * Math.PI / 30; }
                if (thr < 0.05 && vx > 1) { ebF = (rpm / c.redline) * 1200 * c.engineBrake; fdrive -= ebF; } // engine braking (EB map)
            }
            if (kick > 0) { double fk = Math.Min(kick / 0.06, kick / dt); kick -= fk * dt; if (vx > 1) fdrive -= fk; else kick = 0; }
            // ---------- hybrid (MGU-K + battery) ----------
            double fK = 0, harvestW = 0, otW = 0, pKNow = 0; // otW: part of the MGU-K power pKNow paid from the overtake allowance
            otActive = boostActive = false;
            if (c.ersPower > 0)
            {
                if (double.IsNaN(ersEnergy)) ersEnergy = c.ersCapacity;
                double room = Math.Min(c.ersCapacity - ersEnergy, c.ersHarvestLap - ersHarvested);
                if (Math.Abs(vx) < 0.5) standingStart = true; else if (Math.Abs(vx) * 3.6 >= c.ersStartKmh) standingStart = false;
                // the MGU-K comes in smoothly at 50 km/h instead of 350 kW in one step
                ersStart = standingStart ? 0 : c.ersStartRamp > 0 ? Math.Min(1, ersStart + dt / c.ersStartRamp) : 1;
                bool managed = c.ersManage && !standingStart && !reverse && shiftTimer <= 0 && thr > 0.05 && rpm < c.redline && vx > 5 && !dry;
                double pK = managed ? ErsFull(c, vx, ersEnergy / c.ersCapacity, deployMap) : 0, vK = Math.Max(Math.Abs(wR) * c.wheelRadius, 15);
                double kStart = ersStart * ersStart;
                if (pK > 0) pK *= kStart;
                if (managed && thr >= 0.9 && (boostOn || otOn))
                {
                    // boost: full power from the battery (FIA ramp-down only), no map, no superclip
                    if (boostOn && ersEnergy > c.ersCapacity * c.boostMinSoc)
                    {
                        double pb = ErsPowerAt(c, vx, false) * kStart;
                        if (pb > pK) pK = pb;
                        boostActive = true;
                    }
                    // Overtake Mode: what the car would deploy anyway comes from the battery, the rest (up to the overtake
                    // power curve) from this lap's overtake allowance
                    if (otOn && otArmed && OtLeft > 0)
                    {
                        double po = OvertakePowerAt(c, vx) * kStart, pBase = ersEnergy > 0 ? Math.Max(pK, 0) : 0;
                        if (po > pBase) { otW = po - pBase; pK = po; otActive = true; }
                    }
                }
                pKNow = pK;
                double pMax = managed ? pIce / thr : 0;     // ICE crank power available now (with the boost there is)
                if (managed && thr >= 0.9 && pK < 0)
                {
                    // superclipping: flat out, but the MGU-K turns part of the ICE's power into charge
                    if (room > 0) { harvestW = Math.Min(-pK, room / dt); fdrive -= harvestW * c.driveEff / vK; }
                }
                else if (managed && thr < 0.9 && thr * (pMax + Math.Max(0, pK)) <= pMax)
                {
                    // part throttle: the pedal asks for a share of ICE + MGU-K; the ICE alone covers it, running harder
                    // than the pedal, and the MGU-K harvests from what it has left over (same force at the wheels)
                    double extra = thr * Math.Max(0, pK);
                    fdrive += extra * c.driveEff / vK; pIce += extra;
                    if (room > 0) { harvestW = Math.Min(Math.Min(c.ersPartPower, pMax - pIce), room / dt); harvestW = Math.Max(0, harvestW); pIce += harvestW; }
                }
                else if (managed && thr < 0.9)
                {
                    // part throttle above what the ICE can give: ICE flat, the MGU-K adds the rest
                    double pE = thr * (pMax + pK) - pMax;
                    if (ersEnergy > 0) { fK = c.driveEff * pE / vK; fdrive += fK; }
                    double more = (1 - thr) * pMax; fdrive += more * c.driveEff / vK; pIce += more;
                }
                else if (managed && thr >= 0.9 && pK <= 0) { }
                else if (!standingStart && !reverse && shiftTimer <= 0 && thr > 0 && rpm < c.redline && (ersEnergy > 0 || otW > 0))
                {
                    fK = thr * c.driveEff * (managed ? pK : ErsPowerAt(c, vx) * kStart) / Math.Max(Math.Abs(wR) * c.wheelRadius, 15);
                    fdrive += fK;
                }
                else if (!reverse && vx > 5 && room > 0)
                {
                    // charging: on the brakes the motor takes over part of the rear braking (no extra force);
                    // when coasting it adds some drag
                    if (brk > 0) harvestW = Math.Min(c.ersPower, brk * c.brakeForce * (1 - bias) * vx);
                    else if (thr < 0.05) { harvestW = c.ersLiftPower; fdrive -= Math.Min(harvestW / vx, 1500); harvestW = Math.Min(harvestW, 1500 * vx); }
                }
            }

            if (c.fuel && pIce > 0)
            {
                double burnt = Math.Min(fuel, c.fuelFlow / 3600 * pIce / IcePeak(c) * dt);
                fuel -= burnt; fuelThisLap += burnt;
            }

            // ---------- tyres ----------
            double vxs = Math.Max(Math.Abs(vx), 4);
            alphaF = Math.Atan2(vy + a * r, vxs) - delta * (vx >= 0 ? 1 : -1);
            alphaR = Math.Atan2(vy - b * r, vxs);
            if (c.damage) alphaR -= RearToe() * (vx >= 0 ? 1 : -1);   // bent rear toe steers the rear axle
            if (c.relaxLen > 0)
            {
                if (double.IsNaN(relF)) { relF = alphaF; relR = alphaR; if (speed < LowV) relF = relR = 0; } // a car placed at rest starts with straight tyres
                double kr = Math.Min(1, Math.Max(Math.Abs(vx), 2) * dt / c.relaxLen);
                double dRF = (alphaF - relF) * kr, dRR = (alphaR - relR) * kr;
                if (speed < LowV)
                {
                    // Near a standstill the slip angles above (at least 4 m/s assumed, relaxing at at least 2 m/s) turn
                    // the steering angle itself into slip: a car at rest with the wheel turned got full side force and
                    // crept sideways. Here the tyre follows how its contact patch really moves (Pacejka's transient
                    // tyre, which holds through zero speed): relaxation length x d alpha/dt = lateral velocity of the
                    // patch - rolling speed x tan alpha. Turning the wheels at rest moves no patch sideways, so no force;
                    // a push sideways meets the tyre's grip (static friction) as a stiff spring. Blended into the
                    // normal model from 1.5 to 3 m/s, so nothing changes above about 11 km/h.
                    double wN = Clamp((speed - 1.5) / (LowV - 1.5), 0, 1); wN = wN * wN * (3 - 2 * wN);
                    double vfy = vy + a * r, cdl = Math.Cos(delta), sdl = Math.Sin(delta);
                    double uF = vx * cdl + vfy * sdl, latF = vfy * cdl - vx * sdl;
                    double toeR = c.damage ? RearToe() : 0;
                    double uR = vx * Math.Cos(toeR) + (vy - b * r) * Math.Sin(toeR), latR = (vy - b * r) * Math.Cos(toeR) - vx * Math.Sin(toeR);
                    double lF = PeakBa(c) / (c.tireB * Math.Pow(fzF / nF0, -c.slipGrowth)), lR = PeakBa(c) / (c.tireB * Math.Pow(fzR / nR0, -c.slipGrowth)); // peak-grip slip angles
                    double sF = (latF - Math.Abs(uF) * Math.Tan(Clamp(relF, -lF, lF))) * dt / c.relaxLen;
                    double sR = (latR - Math.Abs(uR) * Math.Tan(Clamp(relR, -lR, lR))) * dt / c.relaxLen;
                    relF += dRF * wN + sF * (1 - wN); relR += dRR * wN + sR * (1 - wN);
                    // the patch slides once the tread is deflected to the peak-grip angle; it stores no more than that
                    double ext = 1.6 * wN;
                    relF = Clamp(relF, -lF - ext, lF + ext); relR = Clamp(relR, -lR - ext, lR + ext);
                    // tread damping (Pacejka's low-speed damping term): the spring alone would let the car rock to and
                    // fro after a nudge at rest. Acts on the rate of deflection only, so it is zero in a steady state.
                    double kd = LowDamp / dt * (1 - wN);
                    alphaF = Clamp(relF + kd * sF, -lF - ext, lF + ext); alphaR = Clamp(relR + kd * sR, -lR - ext, lR + ext);
                }
                else { relF += dRF; relR += dRR; alphaF = relF; alphaR = relR; }
            }
            double FyF = -DF * Magic(c, alphaF, fzF / nF0);

            int dir = vx > 0.05 ? 1 : vx < -0.05 ? -1 : 0;
            double Bd = brk * c.brakeForce;
            double kBF = 1, kBR = 1;
            if (c.brakeTemps)
            {
                if (double.IsNaN(brakeTF)) { brakeTF = c.brakeStart; brakeTR = c.brakeStart; }
                kBF = BrakeMu(brakeTF); kBR = BrakeMu(brakeTR);
            }
            double FxF = -dir * Bd * bias * kBF;
            var rF = Combine(FxF, FyF, DF, c.abs);
            FxF = rF.Fx; FyF = rF.Fy;
            lockF = rF.slide; useF = rF.use;

            // ---------- rear axle: wheel speed + combined slip ----------
            double rw = c.wheelRadius, lrR = fzR / nR0;
            double aPk = PeakBa(c) / (c.tireB * Math.Pow(lrR, -c.slipGrowth));
            double kPk = c.peakRatio, vden = Math.Max(Math.Abs(vx), 3);
            double syR = Math.Tan(alphaR) / Math.Tan(aPk);
            double vxNow = vx;
            RearForce RearTyre(double w)
            {
                double kap = (w * rw - vxNow) / vden, sx = kap / kPk, sv = Hypot(sx, syR);
                if (sv < 1e-6) return new RearForce { Fx = 0, Fy = 0, s = 0, kap = kap };
                double f = Magic(c, sv * aPk, lrR);
                double F = DRl * (sv > 1 ? Math.Max(f, 0.75) : f);
                return new RearForce { Fx = F * sx / sv, Fy = -F * syR / sv, s = sv, kap = kap };
            }
            double Iw = c.wheelInertia + c.engineInertia * ratio * ratio;
            double brakeT = Bd * (1 - bias) * rw * kBR;
            if (c.bbw && brakeT > 0)
            {
                // BBW: the MGU-K's harvest torque is part of the rear's share and does not depend on the disc
                // temperature (only the hydraulic rest does); engine braking counts towards the share too
                double shareT = Bd * (1 - bias) * rw, kT = brk > 0 ? Math.Min(shareT, harvestW / Math.Max(Math.Abs(vx), 5) * rw) : 0;
                brakeT = Math.Max(0, kT + (shareT - kT) * kBR - ebF * rw);
            }
            double wv = wR;
            {
                var t0 = RearTyre(wv); double eps = 0.05;
                double slope = Math.Max(0, (RearTyre(wv + eps).Fx - t0.Fx) / eps) * rw;
                double zone = 0.5;
                double bT = brakeT * Clamp(wv / zone, -1, 1);
                double bSlope = brakeT > 0 && Math.Abs(wv) < zone ? brakeT / zone : 0;
                wv += dt * (fdrive * rw - t0.Fx * rw - bT) / (Iw + dt * (slope + bSlope));
            }
            tcActive = false;
            bool rearAbs = false;
            double KapOf(double ww) => (ww * rw - vxNow) / vden;
            if (fdrive > 0 && c.tc != TcMode.Off)
            {
                // low TC lets the tyres spin up more at a standing start (break traction, then hook up)
                double sMax = c.tc == TcMode.High ? 0.95 : 1.15 + 3.5 * Clamp(1 - Math.Abs(vxNow) / 25, 0, 1);
                double sxMax = Math.Max(c.tc == TcMode.High ? 0.3 : 0.6, Math.Sqrt(Math.Max(0, sMax * sMax - syR * syR)));
                if (KapOf(wv) > sxMax * kPk) { wv = (vxNow + sxMax * kPk * vden) / rw; tcActive = true; }
            }
            if (brk > 0 && c.abs && dir > 0)
            {
                double sxMax = Math.Max(0.35, Math.Sqrt(Math.Max(0, 0.9 - syR * syR)));
                if (KapOf(wv) < -sxMax * kPk) { wv = (vxNow - sxMax * kPk * vden) / rw; rearAbs = true; }
            }
            if (Math.Abs(vx) < 0.5 && thr < 0.05 && !reverse) wv = vx / rw; // parked: wheels just roll
            wR = wv;
            var tR = RearTyre(wv);
            double FxRl = tR.Fx, FyR = tR.Fy;
            slipRatioR = tR.kap; Fdrive = fdrive; FxR = FxRl; DR = DRl;
            torque = fdrive > 0 ? fdrive * rw / (ratio * c.driveEff) : 0;
            useR = tR.s <= 1 ? Hypot(FxRl, FyR) / DRl : 1 + 0.1 * Math.Min(tR.s - 1, 1);
            spinR = fdrive > 0 && tR.kap > 1.5 * kPk;
            lockR = brk > 0 && tR.kap < -0.5;
            absActive = (rF.limited || rearAbs) && fdrive <= 0;
            if (c.ersPower > 0)
            {
                // the battery only pays for electric drive that reaches the road (traction control cuts the rest)
                double used = fK > 0 && fdrive > 0 ? Math.Min(fK, Math.Max(FxRl, 0) * fK / fdrive) * Math.Abs(vx) / c.driveEff : 0;
                double got = Math.Min(harvestW * dt, Math.Max(0, Math.Min(c.ersCapacity - ersEnergy, c.ersHarvestLap - ersHarvested)));
                double fromBatt = used;
                if (otW > 0 && pKNow > 0)
                {
                    // the overtake share is paid from the lap's overtake allowance, the rest from the battery
                    double otUse = used * Math.Min(1, otW / pKNow);
                    otEnergy = Math.Max(0, OtLeft - otUse * dt);
                    fromBatt = used - otUse;
                }
                ersEnergy = Clamp(ersEnergy - fromBatt * dt + got, 0, c.ersCapacity); ersHarvested += got;
                ersKw = (used - got / dt) / 1000;
            }

            // ---------- resistances ----------
            double roll = (c.rollRes + surfaceDrag) * m * G;
            // drag on one side only (two wheels in the gravel) pulls the car round towards that side
            double dragL = (wRoll[0] + wRoll[2]) * m * G / 4, dragR = (wRoll[1] + wRoll[3]) * m * G / 4;
            if (c.damage && damage.Punctured)
            {
                // a flat tyre drags on its side and pulls the car that way
                double pl = (Math.Min(1, damage.punctFL) + Math.Min(1, damage.punctRL)) * c.punctRoll * m * G / 4;
                double pr = (Math.Min(1, damage.punctFR) + Math.Min(1, damage.punctRR)) * c.punctRoll * m * G / 4;
                dragL += pl; dragR += pr; roll += pl + pr;
            }
            double resist = (drag + roll) * dir;

            // ---------- integrate ----------
            double cd = Math.Cos(delta), sd = Math.Sin(delta);
            double Fx = FxF * cd - FyF * sd + FxRl - resist - m * G * grade; // gravity pulls the car back on a climb
            double Fy = FxF * sd + FyF * cd + FyR + m * G * Math.Sin(bank); // on a banked road gravity pulls down the slope
            double Mz = a * (FxF * sd + FyF * cd) - b * FyR + (c.track / 2) * (dragL - dragR) * dir;
            if (windy) { Fy += windSide; Mz += windSide * c.sideCp; }
            // brake balance actually reaching the road: front share of all retarding force (brakes, engine, MGU-K)
            double retF = Math.Max(0, -FxF * dir), retR = Math.Max(0, -FxRl * dir);
            biasNow = retF + retR > 1 ? retF / (retF + retR) : bias;
            if (c.chassis)
            {
                // differential: resists the rear wheels turning at different speeds, more so under power or on overrun
                double tLock = c.diffPreload + (fdrive > 0 ? c.diffPower : c.diffCoast) * Math.Abs(FxRl) * rw;
                Mz -= tLock / rw * (c.track / 2) * Math.Tanh(r * c.track / 2 / 0.3);
                // tyre heat: sliding and flexing heat the tyres, air and road cool them
                if (speed > 1)
                {
                    // front braking: the tyre slides at its slip ratio, which grows towards peakRatio as the force nears
                    // the grip (the rear's comes from its wheel speed); a flat 4 % of the braking power left the
                    // fronts 15-20 C below their window while the rears were in it
                    double kapF = c.frontSlipHeat ? Math.Max(0.04, c.peakRatio * Math.Min(1, Math.Abs(FxF) / Math.Max(1, DF))) : 0.04;
                    double pF = Math.Abs(FyF * vxs * Math.Tan(alphaF)) + Math.Abs(FxF) * Math.Abs(vx) * (lockF ? 0.6 : kapF) + 0.6 * c.rollRes * fzF * speed;
                    double pR = Math.Abs(FyR * vxs * Math.Tan(alphaR)) + Math.Abs(FxRl * (wR * rw - vx)) + 0.6 * c.rollRes * fzR * speed;
                    double cool = c.tyreCool0 + c.tyreCoolV * speed, fs = c.frontSlipHeat ? c.tyreFrontSize : 1;
                    if (c.tyreSurface)
                    {
                        // two nodes: sliding heats the thin surface layer, which passes the heat to the carcass;
                        // air and road cool the carcass as before
                        double qF = c.tyreSurfLink * fs * (tyreSF - tyreTF), qR = c.tyreSurfLink * (tyreSR - tyreTR);
                        tyreSF += dt * (c.tyreHeatShare * pF - qF) / (c.tyreSurfCap * fs);
                        tyreSR += dt * (c.tyreHeatShare * pR - qR) / c.tyreSurfCap;
                        tyreTF += dt * (qF - fs * cool * (tyreTF - c.trackTemp)) / (c.tyreHeatCap * fs);
                        tyreTR += dt * (qR - cool * (tyreTR - c.trackTemp)) / c.tyreHeatCap;
                        tyreSF = Clamp(tyreSF, 20, 250); tyreSR = Clamp(tyreSR, 20, 250);
                    }
                    else
                    {
                        tyreTF += dt * (c.tyreHeatShare * pF - fs * cool * (tyreTF - c.trackTemp)) / (c.tyreHeatCap * fs);
                        tyreTR += dt * (c.tyreHeatShare * pR - cool * (tyreTR - c.trackTemp)) / c.tyreHeatCap;
                    }
                    tyreTF = Clamp(tyreTF, 20, 200); tyreTR = Clamp(tyreTR, 20, 200);
                    // dirt: picked up on grass, gravel and astroturf, worn off by driving on the track
                    double dClear = speed * dt / c.dirtClear;
                    dirtF = Clamp(dirtF + (Dirty(wType[0]) + Dirty(wType[1])) * 0.5 * speed * dt / 30 - dirtF * dClear, 0, 1);
                    dirtR = Clamp(dirtR + (Dirty(wType[2]) + Dirty(wType[3])) * 0.5 * speed * dt / 30 - dirtR * dClear, 0, 1);
                    // wear follows the sliding energy; overheated tyres wear faster
                    double life = CompoundTable.Get(c.compound).life * c.tyreLife;
                    double hotF = 1 + Math.Max(0, tyreTF - 115) / 25, hotR = 1 + Math.Max(0, tyreTR - 115) / 25;
                    wearF = Math.Min(1, wearF + pF * c.wearPerJ * hotF / life * dt);
                    wearR = Math.Min(1, wearR + pR * c.wearPerJ * hotR / life * dt);
                }
            }
            if (c.brakeTemps && !double.IsNaN(brakeTF))
            {
                // the brakes take the braking work; the MGU-K takes part of the rear's while it is charging
                double pFb = Math.Abs(FxF) * Math.Abs(vx), pRb = Math.Max(0, Math.Abs(brakeT / rw) * Math.Abs(vx) - harvestW);
                // the rear discs only take the braking the tyres pass to the road (a wheel held at the limit by ABS
                // does not turn the rest into heat), less what the engine braking and the MGU-K take
                if (c.bbw) pRb = Math.Max(0, Math.Min(Math.Abs(brakeT / rw), Math.Max(0, -FxRl * dir)) * Math.Abs(vx) - harvestW);
                double coolB = c.brakeCool0 + c.brakeCoolV * speed;
                double Rad(double tc) => c.brakeRadA * 5.67e-8 * (Math.Pow(tc + 273.15, 4) - Math.Pow(c.brakeAmbient + 273.15, 4));
                brakeTF += dt * (pFb - coolB * (brakeTF - c.brakeAmbient) - Rad(brakeTF)) / c.brakeHeatCap;
                brakeTR += dt * (pRb - coolB * (brakeTR - c.brakeAmbient) - Rad(brakeTR)) / c.brakeHeatCap;
            }
            ax = Fx / m; ay = Fy / m;

            double vx0 = vx;
            vx += (ax + r * vy) * dt;
            vy += (ay - r * vx) * dt;
            r += Mz / Iz * dt;

            if (dir != 0 && Math.Sign(vx) != dir && fdrive == 0) vx = 0;
            if (brk > 0 && Math.Abs(vx0) < 0.3)
            {
                // brakes at (nearly) rest act as static friction: they hold the car against the slope (and the drive)
                // as long as the discs and the tyres can, instead of a pull that only follows the wheels' motion (the
                // car crept down a slope; the front brakes did nothing below 5 cm/s): Coulomb friction, up to what
                // the discs and the tyres can take, against everything else (slope, drive, the rest of the motion)
                double capF = Math.Min(Bd * bias * kBF, DF), capR = Math.Min(Bd * (1 - bias) * kBR, DRl);
                double fFree = Fx - FxF * cd - FxRl + Math.Max(0, fdrive);  // all but the tyres' longitudinal forces
                vx = Approach(vx0 + fFree / m * dt, 0, (capF + capR) / m * dt);
                ax = (vx - vx0) / dt;
                if (vx == 0) wR = 0;
            }
            // rolling resistance keeps acting until the car stands still (below 5 cm/s `dir` is 0 and it did not, so an
            // unbraked car crept on at a few cm/s for ever, turning slowly with the wheels on lock)
            if (dir == 0 && Math.Abs(fdrive) < 1) vx = Approach(vx, 0, roll / m * dt);

            if (speed < 3)
            {
                double k = 1 - speed / 3;
                double rk = vx * Math.Tan(delta) / L;
                double t = Math.Min(1, dt * 15) * k;
                r += (rk - r) * t;
                vy -= vy * t;
            }

            axLong += (ax - axLong) * Math.Min(1, dt * 8);

            double ch = Math.Cos(heading), sh = Math.Sin(heading);
            x += (vx * ch - vy * sh) * dt;
            y += (vx * sh + vy * ch) * dt;
            heading += r * dt;
            wheelSpin += wR * dt;
        }
    }
}
