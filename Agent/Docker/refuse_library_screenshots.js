// Run once at image build. In a seeded run, which has to repeat exactly, screenshot calls in the installed Node
// Playwright, Puppeteer and Python Playwright fail with a message naming browser_screenshot: they capture
// whenever they are called, while the page runs on real time, so their pictures differ from run to run. Without
// a seed they work as usual, and so does everything else in those libraries. FAKETIME is how they can tell: the
// sandbox sets it in the shell of a seeded run alone. A project's own installed copy is not touched. If an
// upgrade moves this code, the build fails here rather than the refusal quietly stopping.
const fs            = require('fs');
const path          = require('path');
const { execSync }  = require('child_process');

const kMessage = 'Screenshots from this library are turned off in this seeded run, because the picture would differ from run to run. Use the browser_screenshot tool. To capture a particular view or state, have the page set it up in its own code, such as an explicit camera position or a URL parameter, and capture that page.';
const kNode    = `if (process.env.FAKETIME) { console.error(${JSON.stringify(kMessage)}); throw new Error(${JSON.stringify(kMessage)}); }`;
const kPython  = `        if __import__("os").environ.get("FAKETIME"):\n            print(${JSON.stringify(kMessage)}, file=__import__("sys").stderr)\n            raise Exception(${JSON.stringify(kMessage)})\n`;

function patch(file, pattern, addition, expected)
{
	const before = fs.readFileSync(file, 'utf8');
	let   count  = 0;
	const after  = before.replace(pattern, match =>
	{
		count++;
		return match + addition;
	});
	if (count !== expected)
		throw new Error(`Expected ${expected} screenshot methods in ${file} and found ${count}. Update ${path.basename(__filename)} for this version.`);
	fs.writeFileSync(file, after);
}

const nodeModules = '/usr/lib/node_modules';

// Locator, ElementHandle and Page. These start with the mask line, which leaves out the Android device's.
patch(`${nodeModules}/playwright/node_modules/playwright-core/lib/coreBundle.js`,
	/async screenshot\(options = \{\}\) \{(?=\s*const mask = options\.mask;)/g, ` ${kNode}`, 3);

const puppeteer = `${nodeModules}/puppeteer/node_modules/puppeteer-core/lib/puppeteer/api`;
patch(`${puppeteer}/Page.js`,          /async screenshot\(userOptions = \{\}\) \{/g, ` ${kNode}`, 1);
patch(`${puppeteer}/ElementHandle.js`, /async screenshot\(options = \{\}\) \{/g,     ` ${kNode}`, 1);

const python = path.join(execSync('python3 -c "import playwright, os; print(os.path.dirname(playwright.__file__))"').toString().trim(), '_impl');
for (const name of ['_page.py', '_element_handle.py', '_locator.py'])
{
	patch(path.join(python, name), /async def screenshot\([\s\S]*?\) -> bytes:\n/g, kPython, 1);
	const compiled = path.join(python, '__pycache__');
	for (const entry of fs.readdirSync(compiled))
	{
		if (entry.startsWith(name.replace('.py', '.')))
			fs.rmSync(path.join(compiled, entry));
	}
}
