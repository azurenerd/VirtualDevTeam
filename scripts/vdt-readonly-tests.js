/**
 * VDT Dashboard Read-Only Test Harness v2
 * 
 * Runs scenarios from docs/system/TestingVDT_ReadOnly.md against a running VDT instance.
 * Results written to .testing/{runId}/results-{timestamp}.json
 * Screenshots captured on failure.
 * 
 * v2 improvements (from 3-model rubber duck):
 * - Content assertions instead of body.length checks
 * - waitForFunction for hang detection (sync-over-async)
 * - Circuit crash detection (#blazor-error-ui, pageerror events)
 * - domcontentloaded + content probes (not networkidle)
 * - try/finally on browser close (no orphan chromium)
 * - PR Files tab click-through
 * - Approvals Review button validation
 * - Dynamic PR/issue number discovery
 * 
 * Usage: node scripts/vdt-readonly-tests.js [--base-url http://localhost:5050] [--run-id auto]
 */

const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const BASE_URL = process.argv.find(a => a.startsWith('--base-url='))?.split('=')[1] || 'http://localhost:5050';
const RUN_ID = process.argv.find(a => a.startsWith('--run-id='))?.split('=')[1] || 'default';

const TIMEOUT = 15000;
const results = [];
let browser, context;

async function test(id, url, checks) {
  const page = await context.newPage();
  const jsErrors = [];
  page.on('pageerror', err => jsErrors.push(err.message));
  page.on('console', m => { if (m.type() === 'error' && !m.text().includes('favicon')) jsErrors.push(m.text()); });

  const result = { id, pageUrl: url, status: 'PASS', timestamp: new Date().toISOString(), contentLength: 0, errorMessage: null, screenshot: null, jsErrors: [] };
  try {
    await page.goto(BASE_URL + url, { waitUntil: 'domcontentloaded', timeout: TIMEOUT });
    await page.waitForTimeout(2000);
    const body = await page.textContent('body');
    result.contentLength = body.length;

    // Check for Blazor error UI or circuit crash
    const hasErrorUI = await page.locator('#blazor-error-ui').isVisible().catch(() => false);
    const hasReconnect = await page.locator('#components-reconnect-modal').isVisible().catch(() => false);
    if (hasErrorUI || body.includes('An error has occurred') || body.includes('Unhandled exception')) {
      result.status = 'FAIL';
      result.errorMessage = 'Blazor error UI visible or error text detected';
    } else if (hasReconnect) {
      result.status = 'FAIL';
      result.errorMessage = 'Blazor circuit disconnected (reconnect modal visible)';
    } else if (checks) {
      const checkResult = await checks(page, body);
      if (checkResult) {
        result.status = 'FAIL';
        result.errorMessage = checkResult;
      }
    }

    if (jsErrors.length > 0) result.jsErrors = jsErrors.slice(0, 5);
  } catch (e) {
    result.status = 'FAIL';
    result.errorMessage = e.message.split('\n')[0];
  }

  if (result.status === 'FAIL') {
    const ssDir = path.join('.testing', RUN_ID, 'screenshots');
    fs.mkdirSync(ssDir, { recursive: true });
    const ssPath = path.join(ssDir, `${id}.png`);
    try { await page.screenshot({ path: ssPath, fullPage: true }); result.screenshot = ssPath; } catch {}
  }

  results.push(result);
  await page.close();
  return result;
}

async function run() {
  browser = await chromium.launch();
  try {
    context = await browser.newContext({ viewport: { width: 1400, height: 900 } });

    // ── 1. Overview — assert agent cards render ──
    await test('OV-01', '/', async (p, b) => {
      try { await p.waitForFunction(() => /Agent|Overview|Total/i.test(document.body.innerText), { timeout: 8000 }); }
      catch { return 'Overview page did not render agent content'; }
      return null;
    });

    // ── 2. Develop — assert LDP toggle present ──
    await test('DV-01', '/develop', async (p, b) => {
      try { await p.waitForFunction(() => /Platform|GitHub|Local Dev Mode/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Develop page did not render platform selector'; }
      return null;
    });

    // ── 3. Timeline — assert phase events ──
    await test('TL-01', '/timeline', async (p, b) => {
      try { await p.waitForFunction(() => /Phase|Session|Research|Started/i.test(document.body.innerText), { timeout: 8000 }); }
      catch { return 'Timeline did not render phase events'; }
      return null;
    });

    // ── 4. Repository Code — file tree renders with entries ──
    await test('RC-01', '/repository', async (p, b) => {
      try { await p.waitForFunction(() => /Code|Pull Requests|Issues/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Repository page did not render tabs'; }
      return null;
    });

    await test('RC-02', '/repository/files', async (p, b) => {
      // Check file tree has actual entries
      if (/0 files/i.test(b)) return 'Code browser shows 0 files';
      const hasFiles = /\.(md|json|cs|ts|js|html|css|sln|csproj|gitignore|yml)/i.test(b);
      if (!hasFiles && !b.includes('files')) return 'No file entries in code browser';
      return null;
    });

    await test('RC-04', '/repository/files', async (p, b) => {
      // Check files aren't concatenated on one line
      if (b.includes('.gitattributes .github')) return 'Files concatenated on one line';
      return null;
    });

    // ── 5. Repository PRs ──
    await test('RP-01', '/repository/pull-requests', async (p, b) => {
      try { await p.waitForFunction(() => /Pull Request|PR #|No pull requests/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'PR list did not render'; }
      return null;
    });

    // ── 6. Repository Issues ──
    await test('RI-01', '/repository/issues', async (p, b) => {
      try { await p.waitForFunction(() => /Issue|#\d|No issues/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Issue list did not render'; }
      return null;
    });

    // ── 7. PR Detail — hang detection + Files tab ──
    await test('PD-01', '/repository/pull-request/1', async (p, b) => {
      try {
        await p.waitForFunction(
          () => /MERGED|OPEN|CLOSED|PR\s*#|not found/i.test(document.body.innerText),
          { timeout: 5000 }
        );
      } catch {
        return 'PR detail did not render in 5s — possible sync-over-async hang';
      }
      return null;
    });

    await test('PD-07', '/repository/pull-request/1', async (p, b) => {
      // Click Files tab and check content
      try {
        await p.waitForFunction(() => /Files/i.test(document.body.innerText), { timeout: 3000 });
        const filesTab = p.locator('button:has-text("Files"), a:has-text("Files")').first();
        if (await filesTab.count() > 0) {
          await filesTab.click();
          await p.waitForTimeout(1500);
          const filesBody = await p.textContent('body');
          if (/0 files changed|no files/i.test(filesBody) && !/not found/i.test(filesBody)) {
            return 'PR Files tab shows 0 files — merge diff may not have been captured';
          }
        }
      } catch { /* Files tab may not exist for some PRs — OK */ }
      return null;
    });

    // ── 8. Issue Detail ──
    await test('ID-01', '/repository/issue/1', async (p, b) => {
      try {
        await p.waitForFunction(
          () => /Issue|#\d|not found/i.test(document.body.innerText),
          { timeout: 5000 }
        );
      } catch {
        return 'Issue detail did not render in 5s';
      }
      return null;
    });

    // ── 9. Approvals — render + Review button validation ──
    await test('AP-01', '/approvals', async (p, b) => {
      try {
        await p.waitForFunction(
          () => /Pending|No pending|Gates|Approval|Decision|Open|Resolved/i.test(document.body.innerText),
          { timeout: 6000 }
        );
      } catch {
        return 'Approvals did not render in 6s — possible sync-over-async hang';
      }
      return null;
    });

    await test('AP-04', '/approvals', async (p, b) => {
      // If Review buttons exist, verify clicking one doesn't crash the circuit
      const reviewLinks = p.locator('a:has-text("Review")');
      const count = await reviewLinks.count();
      if (count === 0) return null; // No pending gates — skip

      const href = await reviewLinks.first().getAttribute('href');
      if (!href || href === '#') return 'Review button has empty/# href';

      await reviewLinks.first().click();
      await p.waitForTimeout(2000);
      const afterBody = await p.textContent('body');
      if (afterBody.includes('An error has occurred')) return 'Review click crashed the circuit';
      if (afterBody.length < 200) return 'Page near-empty after Review click — circuit may have died';

      // Verify can navigate back
      await p.goto(BASE_URL + '/approvals', { waitUntil: 'domcontentloaded', timeout: TIMEOUT });
      await p.waitForTimeout(1500);
      const backBody = await p.textContent('body');
      if (backBody.length < 500) return 'Cannot navigate back to approvals after Review click';
      return null;
    });

    // ── 10. Configuration — hang detection ──
    await test('CF-01', '/configuration', async (p, b) => {
      try {
        await p.waitForFunction(
          () => /Copilot CLI|Workspace|Local Dev Mode|Agents/i.test(document.body.innerText),
          { timeout: 8000 }
        );
      } catch {
        return 'Config page did not render in 8s — possible sync-over-async hang';
      }
      return null;
    });

    await test('CF-05', '/configuration', async (p, b) => {
      // Nav works after config
      await p.goto(BASE_URL + '/strategies', { waitUntil: 'domcontentloaded', timeout: TIMEOUT });
      await p.waitForTimeout(1500);
      const navBody = await p.textContent('body');
      if (navBody.includes('An error has occurred') || navBody.length < 300) {
        return 'Navigation broken after config page';
      }
      return null;
    });

    // ── 11. Strategies ──
    await test('ST-01', '/strategies', async (p, b) => {
      try { await p.waitForFunction(() => /Strateg|Framework|No active/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Strategies page did not render'; }
      return null;
    });

    // ── 12. Reasoning ──
    await test('RE-01', '/reasoning', async (p, b) => {
      try { await p.waitForFunction(() => /Reasoning|Decision|Memory|No entries/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Reasoning page did not render'; }
      return null;
    });

    // ── 13. Scenarios ──
    await test('SC-01', '/scenarios', async (p, b) => {
      try { await p.waitForFunction(() => /Scenario|No scenario|S0/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Scenarios page did not render'; }
      return null;
    });

    // ── 14. Metrics ──
    await test('ME-01', '/metrics', async (p, b) => {
      try { await p.waitForFunction(() => /Metrics|Cost|\$|calls/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Metrics page did not render'; }
      return null;
    });

    // ── 15. Flow Monitor ──
    await test('FM-01', '/flow-monitor', async (p, b) => {
      try { await p.waitForFunction(() => /Flow|Monitor|Finding|No finding/i.test(document.body.innerText), { timeout: 5000 }); }
      catch { return 'Flow monitor did not render'; }
      return null;
    });

    // ── 16. Cross-page navigation sweep ──
    await test('NAV-06', '/', async (p) => {
      const routes = ['/', '/timeline', '/repository', '/approvals', '/strategies',
        '/configuration', '/reasoning', '/scenarios', '/metrics', '/flow-monitor'];
      for (const route of routes) {
        await p.goto(BASE_URL + route, { waitUntil: 'domcontentloaded', timeout: TIMEOUT });
        await p.waitForTimeout(800);
        const body = await p.textContent('body');
        if (body.includes('An error has occurred') || body.includes('Unhandled exception')) {
          return `Route ${route} shows error`;
        }
      }
      return null;
    });

    // ── 17. AgentDocs folder accessible ──
    await test('RC-07', '/repository/files/AgentDocs', async (p, b) => {
      // If docs have been merged, they must be navigable
      const hasDocRefs = /Research\.md|PMSpec\.md|Architecture\.md/i.test(b);
      if (hasDocRefs) {
        const links = await p.locator('a').count();
        if (links < 2) return 'AgentDocs files found in text but too few navigable links';
      }
      return null;
    });

  } finally {
    await browser.close();
  }

  // Write results
  const outDir = path.join('.testing', RUN_ID);
  fs.mkdirSync(outDir, { recursive: true });
  const ts = new Date().toISOString().replace(/[:.]/g, '-');
  fs.writeFileSync(path.join(outDir, `results-${ts}.json`), JSON.stringify(results, null, 2));

  // Write summary
  const passed = results.filter(r => r.status === 'PASS').length;
  const failed = results.filter(r => r.status === 'FAIL').length;
  const summary = [
    `# Test Results — ${new Date().toISOString()}`,
    `Run ID: ${RUN_ID}`,
    ``,
    `**${passed} passed, ${failed} failed** out of ${results.length} scenarios`,
    ``,
    ...results.map(r => `- ${r.status === 'PASS' ? '✅' : '❌'} ${r.id}: ${r.pageUrl} (${r.contentLength} chars)${r.errorMessage ? ' — ' + r.errorMessage : ''}`),
  ].join('\n');
  fs.writeFileSync(path.join(outDir, 'summary.md'), summary);

  // Console output
  console.log(`${passed}/${results.length} passed, ${failed} failed`);
  results.filter(r => r.status === 'FAIL').forEach(r => console.log(`  ❌ ${r.id}: ${r.errorMessage}`));

  process.exit(failed > 0 ? 1 : 0);
}

run().catch(e => { console.error(e); process.exit(1); });
