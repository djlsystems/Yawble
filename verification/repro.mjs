import http from 'node:http';
import { chromium as cr, webkit, firefox } from 'playwright'; const chromium = process.env.WK === 'ff' ? firefox : process.env.WK ? webkit : cr;
const BASE = process.env.BASE || 'http://127.0.0.1:18711';
const MODE = process.env.MODE || 'globe';
const browser = await chromium.launch(process.env.CHROME ? { executablePath: process.env.CHROME, headless: true, args: ['--headless=new'] } : {});
const ctx = await browser.newContext();
const log = [];
ctx.on('request', async (r) => {
  if (!r.isNavigationRequest() && !r.url().includes('/api/auth/me') && !r.url().includes('/api/me')) return;
  const h = await r.allHeaders().catch(() => ({}));
  log.push({ url: r.url().replace(/_c\/[^/]+/, '_c/<cap>'), cookie: !!h.cookie, site: h['sec-fetch-site'], mode: h['sec-fetch-mode'], user: h['sec-fetch-user'], referer: h.referer });
});
ctx.on('response', (r) => { if (r.request().isNavigationRequest()) log.push({ status: r.status(), url: r.url().replace(/_c\/[^/]+/, '_c/<cap>'), location: (r.headers().location || '').replace(/_c\/[^/]+/, '_c/<cap>') }); });
let dropped = 0;
if (process.env.DROP) {
  // Stand-in for a browser that sends the new tab's entry request without the SameSite=Strict cookie.
  await ctx.route(/\/sites\/[^/]+\/[^/]+\/(\?.*)?$/, async (route) => {
    const req = route.request();
    if (req.isNavigationRequest() && (process.env.DROP === 'all' || dropped === 0)) {
      dropped++;
      const h = { ...(await req.allHeaders()) }; delete h.cookie; h['sec-fetch-mode'] = 'navigate';
      const r = await new Promise((ok, no) => { const q = http.request(req.url(), { headers: h }, (res) => { const b = []; res.on('data', (c) => b.push(c)); res.on('end', () => ok({ status: res.statusCode, headers: res.headers, body: Buffer.concat(b) })); }); q.on('error', no); q.end(); });
      return route.fulfill(r);
    }
    return route.continue();
  });
}
const page = await ctx.newPage();
const r = await page.request.post(BASE + '/api/auth/login', { data: { email: process.env.TEST_ADMIN_EMAIL, password: process.env.TEST_ADMIN_PASSWORD } });
console.log('login', r.status());
const cookies = await ctx.cookies(); console.log('cookies', cookies.map(c => ({ name: c.name, sameSite: c.sameSite, httpOnly: c.httpOnly })));
let target;
if (MODE === 'typed') {
  target = await ctx.newPage();
  await target.goto(BASE + '/sites/DemoTeam/team-dashboard/');
} else {
  await page.goto(BASE + '/#/console');
  await page.waitForTimeout(2500);
  await page.getByText('Demo Team', { exact: false }).first().click().catch(e => console.log('noteam', e.message));
  await page.waitForTimeout(2000);
  log.length = 0;
  const pop = ctx.waitForEvent('page');
  if (MODE === 'globe') await page.locator('[data-team-site="team-dashboard"]').first().click();
  else await page.evaluate((u) => window.open(u, '_blank', 'noopener'), '/sites/DemoTeam/team-dashboard/');
  target = await pop;
}
await target.waitForTimeout(6000);
console.log('dropped', dropped, 'navigations', log.filter(l => l.status).length);
if (!process.env.QUIET) console.log(JSON.stringify(log, null, 1)); else console.log(log.filter(l=>l.status).map(l=>l.status+' '+l.url+(l.location?' -> '+l.location:'')).slice(0,12).join('\n'));
console.log('FINAL', target.url().replace(/_c\/[^/]+/, '_c/<cap>'), '|', (await target.textContent('body').catch(() => '')).slice(0, 80));
if(process.env.SHOT){ await target.route('**/*',r=>r.abort());await target.waitForTimeout(300);await target.screenshot({path:process.env.SHOT,timeout:3000}).catch(e=>console.log('loop screenshot unavailable during navigation'));} await browser.close();
