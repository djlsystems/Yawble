// Typing and clicking paced like a person: the mouse travels to an element before it clicks, each key
// is pressed on its own with a jittered delay, and there is a short pause between steps. Never an
// instant fill. WEB_CREDENTIALS_PACE scales every delay (tests use a smaller one; 0 is refused).
const pace = Math.max(0.1, Number(process.env.WEB_CREDENTIALS_PACE) || 1);

const between = (min, max) => min + Math.random() * (max - min);
export const pause = (min = 250, max = 700) => new Promise((r) => setTimeout(r, between(min, max) * pace));

// Where the mouse is, per page, so each move starts where the last one ended.
const at = new WeakMap();

async function moveTo(page, element) {
  await element.scrollIntoViewIfNeeded().catch(() => {});
  const box = await element.boundingBox();
  if (!box) throw new Error('an element to click is not on the page');
  // A point inside the element, not its exact centre.
  const x = box.x + box.width * between(0.3, 0.7);
  const y = box.y + box.height * between(0.35, 0.65);
  const from = at.get(page) ?? { x: between(200, 600), y: between(150, 400) };
  const steps = Math.max(8, Math.round(Math.hypot(x - from.x, y - from.y) / 25));
  await page.mouse.move(from.x, from.y);
  await page.mouse.move(x, y, { steps });
  at.set(page, { x, y });
  await pause(80, 220);
  return { x, y };
}

export async function click(page, element) {
  await moveTo(page, element);
  await page.mouse.down();
  await new Promise((r) => setTimeout(r, between(50, 130) * pace));
  await page.mouse.up();
  await pause();
}

// Clicks into the field and types the text one key at a time.
export async function type(page, element, text) {
  await click(page, element);
  // An element already holding text (a remembered username) is cleared as a person would.
  const existing = await element.inputValue().catch(() => '');
  if (existing) {
    await page.keyboard.press('Control+A');
    await pause(60, 160);
    await page.keyboard.press('Backspace');
    await pause(120, 300);
  }
  for (const ch of String(text)) {
    await page.keyboard.type(ch);
    // Mostly quick keys, now and then a longer gap, as a typist's are.
    const gap = Math.random() < 0.08 ? between(250, 500) : between(55, 190);
    await new Promise((r) => setTimeout(r, gap * pace));
  }
  await pause(200, 500);
}

export async function pressEnter(page) {
  await pause(150, 350);
  await page.keyboard.press('Enter');
}
