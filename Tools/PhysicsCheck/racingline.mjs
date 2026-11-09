// Racing line + speed profile from racingline.js on the default track: writes off,x,y,v per point.
import { writeFileSync } from 'fs';
const { buildTrack } = await import('/home/claude/RacingGame/track.js');
const { buildRacingLine, speedProfile } = await import('/home/claude/RacingGame/racingline.js');
const { DEFAULT_CAR } = await import('/home/claude/RacingGame/physics.js');
const track = buildTrack(), line = buildRacingLine(track), v = speedProfile(line, { ...DEFAULT_CAR });
writeFileSync(new URL('./racingline_golden.csv', import.meta.url), line.map((p, i) => [p.off, p.x, p.y, v[i]].join(',')).join('\n'));
console.log('points', line.length);
