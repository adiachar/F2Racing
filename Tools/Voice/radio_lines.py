# radio_lines.py — every team-radio line: (id, speaker, text, speed).
# speaker "E" = race engineer (calm British voice), "D" = the player's driver.
# The ids are what TeamRadio.cs asks for; keep them in step with the constants there.
# Only made-up content: no real teams, drivers or sponsors.

NUM = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"]
POS = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
       "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
       "twenty one", "twenty two"]

LINES = [
    # start
    ("radio_check", "E", "Radio check.", 1.05),
    ("lights", "E", "Lights out, good luck.", 1.05),
    ("good_start", "E", "Good start, good start.", 1.1),
    # position
    *[(f"p{i + 1}", "E", f"P {w}.", 1.15) for i, w in enumerate(POS)],
    ("good_move", "E", "Good move.", 1.05),
    ("lost_place", "E", "Lost a place. Keep your head down.", 1.05),
    ("leading", "E", "You're leading the race!", 1.05),
    # gaps, built from parts: gap_ahead + n1 + s2 = "Gap to the car ahead, one point, two seconds."
    ("gap_ahead", "E", "Gap to the car ahead,", 1.1),
    ("gap_behind", "E", "Gap to the car behind,", 1.1),
    *[(f"n{i}", "E", f"{w} point,", 1.2) for i, w in enumerate(NUM)],
    *[(f"s{i}", "E", f"{w} seconds.", 1.15) for i, w in enumerate(NUM)],
    ("catching", "E", "You're catching him. Keep pushing.", 1.05),
    ("pulling_away", "E", "Gap is growing. Good job.", 1.05),
    ("defend", "E", "Car behind is close. Defend.", 1.05),
    ("within_one", "E", "Within one second.", 1.05),
    ("overtake", "E", "Overtake available.", 1.05),
    ("boost", "E", "Use your boost.", 1.05),
    # laps
    ("laps20", "E", "Twenty laps to go.", 1.05),
    ("laps15", "E", "Fifteen laps to go.", 1.05),
    ("laps10", "E", "Ten laps to go.", 1.05),
    ("laps5", "E", "Five laps to go.", 1.05),
    ("laps3", "E", "Three laps to go.", 1.05),
    ("laps2", "E", "Two laps to go.", 1.05),
    ("halfway", "E", "Halfway. Good work so far.", 1.05),
    ("last_lap", "E", "Last lap. Bring it home.", 1.05),
    ("final_lap", "E", "Final lap, final lap. Give it everything.", 1.1),
    # timing
    ("fastest_lap", "E", "Fastest lap! Purple, purple.", 1.1),
    ("purple_sector", "E", "Purple sector.", 1.05),
    ("pb", "E", "Personal best. Nice lap.", 1.05),
    ("good_pace", "E", "Good pace. Keep it up.", 1.05),
    # tyres
    ("fronts_cold", "E", "Fronts are cold, push to bring them in.", 1.05),
    ("rears_cold", "E", "Rears are cold. Careful on traction.", 1.05),
    ("fronts_hot", "E", "Fronts overheating. Take care in the slow corners.", 1.05),
    ("rears_hot", "E", "Rears overheating, manage them.", 1.05),
    ("tyres_window", "E", "Tyres are in the window now.", 1.05),
    ("wear_mid", "E", "The tyres are wearing now. Look after them.", 1.05),
    ("wear_high", "E", "Tyres are close to the cliff.", 1.05),
    # battery
    ("batt_low", "E", "Battery low, harvest.", 1.05),
    # pit
    ("box", "E", "Box, box.", 1.05),
    ("box_confirm", "E", "Box this lap, confirm.", 1.05),
    ("stay_out", "E", "Stay out, stay out.", 1.05),
    ("pit_ready", "E", "Copy. We're ready for you.", 1.05),
    ("limiter", "E", "Pit limiter, pit limiter.", 1.1),
    ("good_stop", "E", "Good stop. Push now, push.", 1.1),
    ("box_inters", "E", "Box for intermediates.", 1.05),
    ("box_wets", "E", "Box for the full wet tyres.", 1.05),
    ("box_slicks", "E", "The track is drying. Box for slick tyres.", 1.05),
    ("box_wing", "E", "Box, box. We'll change the wing.", 1.05),
    ("puncture", "E", "You have a puncture. Box, box.", 1.05),
    ("cheap_stop", "E", "Box, box. Cheap stop under the safety car.", 1.1),
    # flags and penalties
    ("track_limits", "E", "Track limits. Watch the white lines.", 1.05),
    ("tl_last", "E", "That's your last warning for track limits.", 1.05),
    ("penalty", "E", "We have a five second penalty.", 1.05),
    ("jump_start", "E", "Jump start. Five second penalty.", 1.05),
    ("yellow", "E", "Yellow flag ahead. Careful.", 1.05),
    ("sc", "E", "Safety car, safety car.", 1.05),
    ("vsc", "E", "V S C, V S C. Keep to the delta.", 1.05),
    ("vsc_ending", "E", "V S C ending. Get ready.", 1.05),
    ("sc_in", "E", "Safety car in this lap.", 1.05),
    ("green", "E", "Green flag, green flag. Go, go!", 1.1),
    ("blue", "E", "Blue flags. Let him through.", 1.05),
    # incidents, weather
    ("damage_seen", "E", "Copy, we can see the damage.", 1.05),
    ("are_you_ok", "E", "Are you okay?", 1.05),
    ("no_worries", "E", "No worries. We go again.", 1.05),
    ("rain", "E", "Rain is starting. Take care.", 1.05),
    ("rain_heavy", "E", "It's raining hard now.", 1.05),
    ("copy", "E", "Copy.", 1.05),
    # result
    ("win", "E", "P one! P one! You've won the race! Fantastic job!", 1.1),
    ("champion", "E", "And you are the world champion! Unbelievable, mate!", 1.1),
    ("podium", "E", "Podium! Great job today, really great job.", 1.05),
    ("points", "E", "Good points today. Well done.", 1.05),
    ("consolation", "E", "Tough one today. We'll come back stronger.", 1.05),
    # the driver
    ("d_copy", "D", "Copy.", 1.05),
    ("d_understood", "D", "Understood.", 1.05),
    ("d_confirm", "D", "Confirm.", 1.05),
    ("d_loud_clear", "D", "Loud and clear.", 1.05),
    ("d_tyres_gone", "D", "Tyres are gone!", 1.1),
    ("d_no_grip", "D", "I've got no grip!", 1.1),
    ("d_yes", "D", "Yes! Get in there!", 1.1),
    ("d_sorry", "D", "Ugh, sorry guys.", 1.0),
    ("d_ok", "D", "Yeah, I'm okay.", 1.05),
    ("d_damage", "D", "I think I've got damage.", 1.1),
    ("d_thanks", "D", "Thanks guys! The car was amazing today!", 1.1),
    ("d_rain", "D", "It's starting to rain.", 1.1),
]
