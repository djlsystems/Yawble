/**
 * A CRON SCHEDULE IN WORDS, IN THE PERSON'S OWN TIME ZONE: "weekdays at 8:00 AM London time,
 * 3:00 AM yours". The schedule's own zone comes first, because that is the clock it keeps (a London
 * schedule stays at 8:00 London time across both clocks changing); the reader's clock follows, for
 * its next occurrence, so it says what an install's "first runs at" says. A time of day is in the
 * browser's own format; nothing here picks a 12- or 24-hour clock.
 *
 * Only the shapes a person writes are put in words: a fixed minute at one or more fixed hours on
 * every day, weekdays, weekends or named days, and "every N minutes" or "every N hours". Anything
 * else is null, and the caller shows the raw cron, which is always under Advanced anyway.
 */

const DayNames = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const DayAbbreviations = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];

/** The browser's IANA zone, the reader's own clock. */
export function browserZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
}

const isUtc = (zone: string | null | undefined) => !zone || /^(Etc\/)?(UTC|GMT|Zulu|UCT)$/i.test(zone);

/** "London time" for Europe/London, "New York time" for America/New_York, "UTC" for UTC. */
export function zoneWords(zone: string | null | undefined): string {
  if (isUtc(zone)) return 'UTC';
  const city = zone!.split('/').pop()!.replace(/_/g, ' ');
  return `${city} time`;
}

/** Whether two zone names are the same clock. */
export function sameZone(a: string | null | undefined, b: string): boolean {
  if (isUtc(a)) return isUtc(b);
  return a === b;
}

/** The wall clock in `zone` at `instant`, as numbers. */
function wallClock(instant: number, zone: string) {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: zone,
    year: 'numeric',
    month: 'numeric',
    day: 'numeric',
    hour: 'numeric',
    minute: 'numeric',
    second: 'numeric',
    hourCycle: 'h23',
  }).formatToParts(new Date(instant));
  const read = (type: string) => Number(parts.find((part) => part.type === type)?.value ?? 0);
  return { year: read('year'), month: read('month'), day: read('day'), hour: read('hour'), minute: read('minute'), second: read('second') };
}

/** How far `zone` is ahead of UTC at `instant`, in milliseconds. */
function offsetAt(instant: number, zone: string): number {
  const wall = wallClock(instant, zone);
  return Date.UTC(wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, wall.second) - Math.floor(instant / 1000) * 1000;
}

/** The instant the clock in `zone` reads this wall time. */
function instantOf(year: number, month: number, day: number, hour: number, minute: number, zone: string): number {
  const guess = Date.UTC(year, month - 1, day, hour, minute);
  const first = guess - offsetAt(guess, zone);
  const second = guess - offsetAt(first, zone);
  return second;
}

/** A time of day in the browser's format, in `zone`: "8:00 AM" in en-US, "08:00" in en-GB. */
export function clockWords(instant: number, zone?: string): string {
  return new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit', ...(zone ? { timeZone: zone } : {}) })
    .format(new Date(instant))
    .replace(/\s+/g, ' ');
}

/** A cron day-of-week field as day numbers (0 Sunday), or null for one not put in words. */
function daysOf(field: string): number[] | null {
  if (field === '*' || field === '?') return [0, 1, 2, 3, 4, 5, 6];
  const days = new Set<number>();
  for (const piece of field.toUpperCase().split(',')) {
    const range = piece.split('-');
    const day = (text: string) => {
      const named = DayAbbreviations.indexOf(text);
      if (named >= 0) return named;
      if (!/^\d$/.test(text)) return null;
      const number = Number(text);
      return number === 7 ? 0 : number <= 6 ? number : null;
    };
    if (range.length === 1) {
      const one = day(range[0]!);
      if (one === null) return null;
      days.add(one);
    } else if (range.length === 2) {
      const from = day(range[0]!);
      const to = day(range[1]!);
      if (from === null || to === null || from > to) return null;
      for (let d = from; d <= to; d++) days.add(d);
    } else return null;
  }
  return [...days].sort();
}

function daysWords(days: number[]): string {
  const key = days.join(',');
  if (key === '0,1,2,3,4,5,6') return 'every day';
  if (key === '1,2,3,4,5') return 'weekdays';
  if (key === '0,6') return 'weekends';
  const names = days.map((day) => `${DayNames[day]}s`);
  return `on ${names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`}`;
}

function listWords(items: string[]): string {
  return items.length <= 1 ? (items[0] ?? '') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

/** The next instant, after `now`, the clock in `zone` reads `hour:minute` on one of `days`. */
function nextOccurrence(hour: number, minute: number, days: number[], zone: string, now: number): number | null {
  const today = wallClock(now, zone);
  for (let ahead = 0; ahead <= 8; ahead++) {
    const date = new Date(Date.UTC(today.year, today.month - 1, today.day + ahead));
    if (!days.includes(date.getUTCDay())) continue;
    const at = instantOf(date.getUTCFullYear(), date.getUTCMonth() + 1, date.getUTCDate(), hour, minute, zone);
    if (at > now) return at;
  }
  return null;
}

/** The calendar day an instant falls on in `zone`, as a number to compare. */
function dayNumber(instant: number, zone: string): number {
  const wall = wallClock(instant, zone);
  return Date.UTC(wall.year, wall.month - 1, wall.day) / 86_400_000;
}

/**
 * A cron expression (five fields, or six with seconds first) in its zone, in words, or null for a
 * shape not put in words. `yourZone` is the reader's, the browser's when left out.
 */
export function cronWords(
  cron: string | null | undefined,
  zone: string | null | undefined,
  now: Date = new Date(),
  yourZone: string = browserZone(),
): string | null {
  if (!cron?.trim()) return null;
  const parts = cron.trim().split(/\s+/);
  if (parts.length !== 5 && parts.length !== 6) return null;
  const [seconds, minutes, hours, dayOfMonth, month, dayOfWeek] = parts.length === 6 ? parts : ['0', ...parts];
  if (dayOfMonth !== '*' && dayOfMonth !== '?') return null;
  if (month !== '*') return null;
  if (seconds !== '0' && seconds !== '00') return null;

  const step = (field: string) => (/^\*\/\d+$/.test(field) ? Number(field.slice(2)) : null);
  if (dayOfWeek === '*' || dayOfWeek === '?') {
    const everyMinutes = step(minutes!);
    if (everyMinutes !== null && hours === '*') return everyMinutes === 1 ? 'every minute' : `every ${everyMinutes} minutes`;
    const everyHours = step(hours!);
    if (everyHours !== null && /^\d+$/.test(minutes!)) {
      return everyHours === 1 ? 'every hour' : `every ${everyHours} hours`;
    }
  }

  if (!/^\d{1,2}$/.test(minutes!) || !/^\d{1,2}(,\d{1,2})*$/.test(hours!)) return null;
  const minute = Number(minutes);
  const hourList = hours!.split(',').map(Number);
  if (minute > 59 || hourList.some((hour) => hour > 23)) return null;
  const days = daysOf(dayOfWeek!);
  if (!days || days.length === 0) return null;

  const theirZone = isUtc(zone) ? 'UTC' : zone!;
  const occurrences = hourList
    .map((hour) => nextOccurrence(hour, minute, days, theirZone, now.getTime()))
    .filter((at): at is number => at !== null);
  if (occurrences.length !== hourList.length) return null;

  const theirs = listWords(occurrences.map((at) => clockWords(at, theirZone)));
  const head = `${daysWords(days)} at ${theirs} ${zoneWords(zone)}`;
  if (sameZone(zone, yourZone)) return `${head} (your time)`;

  const yours = listWords(
    occurrences.map((at) => {
      const shift = dayNumber(at, yourZone) - dayNumber(at, theirZone);
      const when = clockWords(at, yourZone);
      return shift < 0 ? `${when} the day before` : shift > 0 ? `${when} the next day` : when;
    }),
  );
  return `${head}, ${yours} yours`;
}

/** The raw schedule, for Advanced: "cron 0 0 8 * * 1-5 (Europe/London)". */
export function rawCron(cron: string, zone: string | null | undefined): string {
  return `cron ${cron} (${zone || 'UTC'})`;
}
