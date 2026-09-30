/**
 * A NUMBER SETTING'S BOUNDS, SAID EARLY. A manifest's `number` field may declare `min`, `max` and
 * `integer: true`; the Host refuses a value outside them on every writer (settings, hire, solution
 * install and update), naming the field and the bound. The forms say the same thing FIRST - in the
 * field's hint, and inline before sending, the way a required field is held - so a stray keystroke
 * never reaches the route. THE HOST IS STILL THE CHECK; this only says it earlier.
 *
 * Shared by the settings form (Member settings, Add member, the Solutions panel) and the install
 * wizard, so the three say a bound in the same words.
 */

/** What a field declares. Absent or null: no bound on that side. */
export interface NumberBounds {
  min?: number | null;
  max?: number | null;
  integer?: boolean | null;
}

const has = (bound: number | null | undefined): bound is number => typeof bound === 'number' && Number.isFinite(bound);

/** Whether the field declares any bound at all. */
export const isBounded = (bounds: NumberBounds) => has(bounds.min) || has(bounds.max) || bounds.integer === true;

/** "Between 0 and 100, whole numbers only." - or null when the field declares nothing. */
export function boundsHint(bounds: NumberBounds): string | null {
  const whole = bounds.integer === true;
  let range: string | null = null;

  if (has(bounds.min) && has(bounds.max)) range = `Between ${bounds.min} and ${bounds.max}`;
  else if (has(bounds.min)) range = `At least ${bounds.min}`;
  else if (has(bounds.max)) range = `At most ${bounds.max}`;

  if (range === null) return whole ? 'Whole numbers only.' : null;
  return whole ? `${range}, whole numbers only.` : `${range}.`;
}

/** A number box's value (text as typed, or a number) as the number it names; null for blank or not a number. */
function asNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string' || value.trim() === '') return null;

  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

/**
 * Why `value` cannot be saved for the field `name`, naming the field and the bound - or null when it
 * can. A blank box is not this rule's business (required is), nor is text that names no number: the
 * Host's refusal names that.
 */
export function boundsProblem(name: string, bounds: NumberBounds, value: unknown): string | null {
  const number = asNumber(value);
  if (number === null) return null;

  if (bounds.integer === true && !Number.isInteger(number)) {
    return `${name} takes whole numbers only; ${number} is not one.`;
  }
  if (has(bounds.min) && number < bounds.min) {
    return `${number} is out of range for ${name}: it must be at least ${bounds.min}.`;
  }
  if (has(bounds.max) && number > bounds.max) {
    return `${number} is out of range for ${name}: it must be at most ${bounds.max}.`;
  }

  return null;
}
