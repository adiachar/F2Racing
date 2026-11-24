# Career — "The Promise"

The Championship mode is now a saved career: a rookie, a promise to his late mentor, and a seven-time champion in the way.
Code: `Season.cs` (save, roster, tiers, round progress), `Career.cs` (goals), `Story.cs` (story cards), `SeasonUI.cs` (hub,
cards, contract page, quali results), `CareerDifficulty.cs` (presets), `CareerSession.cs` (GameManager hooks).

## Flow
1. Home: in Championship mode the black button is **Start career / Continue career** and opens the career (no circuit pick).
2. New career: prologue cards -> **Contract offers** (the six midfield teams + the difficulty preset) -> **Sign** -> welcome
   cards (team principal, Mia) -> round 1 story + goals -> **hub**.
3. Hub: round X of 10, calendar strip, next-race line (circuit, tier car, laps, grid state), your position / points / team,
   gap to Raske, this round's goals and the season goals (boxes), drivers top 5 + you, teams top 3 + yours + the rival team.
   Black button: **Qualifying** -> (results; grid saved) -> **Race**. Links: Back, Practice, Skip qualifying (= start from
   the back), Difficulty (tap = next preset), Setup (laps, quali length, weather, time of day). "All ›" = full tables and
   **New career** (asks twice).
4. Race finish -> results -> **Next round** -> result card (goals ticked, +XP) + a reaction -> next round's story + goals -> hub.
5. After round 10: season-end cards. Junior tier top 3 = promoted (Rookie -> Challenger -> Premier). Premier champion =
   "Promise kept". Otherwise "The rivalry continues". **Next season** keeps the team, resets the table.

Rules: only the 10 calendar circuits, in order; the tier sets the car class (Rookie = Apex Rookie, Challenger = Apex
Challenger, Premier = Apex Premier; the player's own class comes back for quick races). The full 22-car grid.

### No retries
- Qualifying: no restart button, no Restart setting. Leaving it (or killing the app) = no time = start from the back.
- Race: restart button / Restart setting only on Easy and Medium (Custom: "Career restarts"). On Hard+ leaving the race (or
  killing the app) = retired (DNF, no points; the others finish in grid order).
- During a career session the settings that would restart it (car, track, rivals, rival level, grid, time of day) and
  the damage / difficulty rows are locked.

## Save format
`Application.persistentDataPath/season.json` (JsonUtility), mirrored in PlayerPrefs `seasonJson` (WebGL). Saved after
every session (quali start, grid set, race start, race recorded) and every story step.
```
SeasonData { version 2, team, displaced, round (0-9; 10 = season over), points[22], prevRank[22],
  results: [SeasonRound { circuit, order[] (roster ids), grid[], fastest, quali (player's quali pos, 0 = none),
                          diff (difficulty), goals (bit mask met), goalCount, dnf }],
  tier (0 Rookie, 1 Challenger, 2 Premier), seasonNo, rivalTeam,
  stage (0 none, 1 qualifying started, 2 grid set, 3 race started), qualiGrid[] (roster ids), qualiPos, raceRestarts,
  goals: [CareerGoal { kind (0 finish top n, 1 qualify top n, 2 ahead of who), n, who, text, done }], goalsFor,
  prologue, storyPre, storyPost, endShown, bonusXp,
  history: [CareerSeason { seasonNo, tier, team, rank, points, champion, promoted }] }
```
Roster id 0 = the player, id k = `DriverProfile.Grid[k-1]`. Id 1 (the grid's fastest, Argent) is shown as **N. Raske**
in the junior tiers and **V. Raske** in Premier (same pace and AI; HUD code RAS). A version-1 save (the old championship)
is taken over as a Premier season.

## Difficulty (Settings > ASSISTS "Difficulty", the hub, the contract page, the career's Setup step)

| | Easy | Medium | Hard | Pro | Master | Custom |
|---|---|---|---|---|---|---|
| Rival level | Easy (0.82) | Medium (0.89) | Hard (0.95) | Real (1.0) | Real (1.0) | yours |
| Damage, quick race | Off | Visual | Full | Full | Full | yours |
| Damage, career | Visual | Visual | Full | Full | Full | yours, at least Visual |
| Racing line | Full | Full | Braking only | Off | Off | yours |
| Braking assist | High | Low | Off | Off | Off | yours |
| Steering assist | High | Low | Off | Off | Off | yours |
| ABS | On | On | On | Off | Off | yours |
| Traction control | High | Low | Low | Low | Off | yours |
| Career race restarts | Yes | Yes | No | No | No | "Career restarts" |
| XP multiplier | ×0.8 | ×1 | ×1.25 | ×1.5 | ×2 | from the settings (0.6-2) |

Custom XP: rival level (0.8 / 1 / 1.25 / 1.5) + line off 0.15 (braking only 0.05) + ABS off 0.1 + TC off 0.1 (High -0.05)
- 0.05 per level of braking / steering assist. Changing any preset setting by hand turns the preset into Custom. Existing
players start on Custom (nothing changes for them). The multiplier applies to all race XP (`Progression.WithDifficulty`)
and the career's goal bonus (+50 XP per round goal).

## Goals
Round goals (two, frozen when the round is first shown; positions adapt to your last three results, never easier):

| Round | Goal 1 | Goal 2 |
|---|---|---|
| 1 Japan | Score points (top 10) | Beat your teammate |
| 2 Monaco | Qualify top 10 | Score points |
| 3 Spain | Beat your teammate | Top 8 |
| 4 Austria | Beat the rival team's lead driver | Qualify top 10 |
| 5 Britain | Top 8 | Beat your teammate |
| 6 Belgium | Finish ahead of Raske | Top 8 |
| 7 Hungary | Qualify top 8 | Beat the rival team's lead driver |
| 8 Netherlands | Top 6 | Beat your teammate |
| 9 Italy | Finish ahead of Raske | Qualify top 6 |
| 10 USA | Win (in the title fight: ≤25 pts behind) or top 5 | Finish ahead of Raske |

Season goals: beat the rival team (nearest stronger team, never Argent) in the teams' table; top 3 = promotion (junior
tiers) / win the world title (Premier); beat Raske in a race.

## Story

**Cast.** *You* — a rookie from a harbour town. *Teo Varga* — your late mentor; twenty years ago he was one corner from
the world title when Viktor Raske dived into a gap that wasn't there. *Viktor Raske* — seven-time champion, Argent,
thirteen seasons at the top, cold and precise, chasing an eighth title. *Nico Raske* — his son, Argent junior, your
rival in the Rookie and Challenger tiers. *Mia Varga* (10) — Teo's granddaughter, your biggest fan. *Lena Brandt* — your
team principal.

**Prologue.**
1. Twenty years ago, Teo Varga was one corner from the world title. — Viktor Raske dived for a gap that wasn't there. Teo hit the wall. Raske took the trophy.
2. He never raced at the top again. — He came home, built a kart track by the harbour, and found a kid who always wanted one more lap. You.
3. LAST WINTER — "Bring it home, kid." — His old helmet is in your bag now. You promised.
4. RASKE — "Teo Varga? I don't remember him." — Thirteen seasons at the top. Cold, precise, unbeaten. He wants an eighth title.
5. ROOKIE TIER — Every champion starts somewhere. — Ten rounds. The top three move up. Raske's son Nico is on the grid, with his father's elbows.

**Welcome.** Lena: "Welcome to {team}. We're not here to make up the numbers." (Season goal: beat {rival team}.) Mia:
"Grandpa said you brake later than anyone. Prove it!!"

**Before each round** (chapter card with the red round number, one voice, the goals):
1. Suzuka. Your first race. — Lena's orders: bring the car home. Mia has set three alarms. / Lena: "Clean race, clean car. The rest will come."
2. Monaco. No room for mistakes. — Teo won here once, in the rain. / Raske: "Monaco forgives nobody. Least of all rookies." (Nico: "Nice helmet. Shame about the driver.")
3. Barcelona. Nowhere to hide. — Your teammate wants to prove he is still number one. / Mia: "Grandpa's café has your poster up now. The baker says it's crooked. It's not."
4. Austria. A short lap, long straights. — {rival team} brought a new floor. / Lena: "{rival team} are the team to beat this year. Start today."
5. Britain. Fast, cold, flat out. — The paddock has started to say your name the right way. / Raske: "He is fast. Fast is not enough." (Nico: "My father says rookies learn the hard way. I say they don't learn at all.")
6. Spa. Teo's favourite. — 'Eau Rouge is a question. Answer it flat.' / Teo (memory): "Flat through Eau Rouge, kid. Your heart lifts before the car does."
7. Hungary. Tight, hot, relentless. — Saturday decides Sunday. / Lena: "Track position is everything here. Nail qualifying."
8. Zandvoort. Banked corners, orange smoke. — Mia's whole class is watching. / Mia: "I'm calling my school project 'The Promise'."
9. Monza. The temple of speed. — Raske won here last year by eleven seconds. / Raske: "I have seven trophies. Give me a reason to remember your name." (Nico: "Monza is about courage. You'll need to borrow some.")
10. Austin. The last round. (In the title fight: "Everything comes down to this. N points behind. Teo's helmet is in the garage.") / Mia: "Whatever happens, Grandpa would be proud. (But WIN.)"

**After each round**: the result card (P, points, championship position, goals MET / MISSED, +XP), then one voice: a win
-> Mia ("YOU WON!!! I screamed so loud the dog hid under the bed.") and, if you beat him, Raske; a podium -> Mia; beat
Raske -> Raske ("Enjoy it. It will not happen twice." / "Fine. Now I remember the name."); points -> Lena ("That's the
drive we signed you for."); Raske won and you were out of the points -> Raske ("Rookies learn the hard way."); otherwise
Lena ("Reset. Debrief at eight. We go again.") and sometimes Mia ("Grandpa said the bad days are the ones that make
you."); retired on Hard+ -> Lena ("You walked away from a race. We don't do that here.").

**Season end.**
- Premier champion: WORLD CHAMPION — **Promise kept.** You cross the line, look up at the sky and say his name. / Raske,
  behind the podium: "Teo Varga. I remember him now." / Mia: "Grandpa's helmet is on the shelf next to your trophy." /
  You brought it home. Next season you race with number 1. Raske wants it back.
- Junior tier top 3: Promoted to Challenger ("Nico Raske is coming with you") / Promoted to Premier ("The world title.
  And Viktor Raske." Raske: "So Teo's boy made it. Let's see how long he stays.") + Mia.
- Otherwise: **The rivalry continues.** Lena: "Same goals, sharper knives. Next year." Mia: "Grandpa lost too,
  sometimes. He never stopped. Neither will you."

## Story films (StoryFilm.cs)
Settings > GAME "Story": **Film** (default) plays each run of story cards as short real-time 3D scenes (the circuit loaded behind the menu, the game's cars and People rigs; the card's lines as captions; tap = next scene, SKIP ends it), **Cards** = the text cards, **Off** = only the goals cards. Scenes: prologue crash in the rain (Teo vs Raske, slow motion, the wall), Teo at his car, the player at his car ("Bring it home, kid"), Raske at the Argent garage, Mia waving in the pit lane; per round Lena's briefing / debrief, Mia waving / cheering, Raske's garage or his car leaving, a Teo memory lap, a chapter crane over the team's box, the season-end celebration. The goals cards stay cards. Shots: `story=prologue&t=1,5,10` (DebugShots.cs).
