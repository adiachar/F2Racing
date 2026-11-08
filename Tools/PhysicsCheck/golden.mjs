// Runs the scenarios through physics.js and writes the state every 0.05 s.
import { readFileSync, writeFileSync } from 'fs';
const { createCar, step } = await import('/home/claude/RacingGame/physics.js');
const H = 1 / 240, sc = JSON.parse(readFileSync(new URL('./scenarios.json', import.meta.url)));
const out = [];
for (const s of sc) {
  const car = createCar(s.cfg); car.vx = s.vx0; car.gear = s.gear0;
  let n = 0;
  for (const [dur, s0, t0, b0, s1, t1, b1] of s.segs) {
    const steps = Math.round(dur / H);
    for (let i = 0; i < steps; i++) {
      const f = i / steps;
      step(car, { steer: s0 + (s1 - s0) * f, throttle: t0 + (t1 - t0) * f, brake: b0 + (b1 - b0) * f }, H);
      if (++n % 12 === 0) out.push([s.name, n, car.x, car.y, car.heading, car.vx, car.vy, car.r, car.wR, car.rpm, car.gear].join(','));
    }
  }
}
writeFileSync(new URL('./golden.csv', import.meta.url), out.join('\n'));
console.log('rows', out.length);
