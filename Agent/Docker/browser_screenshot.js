// The browser_screenshot tool's browser side (Agent/Tools/BrowserScreenshotTool.cs), the one way to screenshot a
// page in the sandbox.
// Any page, as it is: the browser runs normally, except that time only moves when this script says so, in
// exact timesteps, so a step count and a step length name one exact moment with the same bytes every run.
//
// Each step: set the page's clock, let Chromium's virtual time run that long (timers fire), then issue one
// frame with HeadlessExperimental.beginFrame (requestAnimationFrame runs, CSS animations and transitions
// advance, the compositor draws). Virtual time holds still while a network fetch is pending, so a slow
// download changes nothing. The last frame is the picture.
//
// Steps are whole tenths of a millisecond: Chromium rounds animation time to 0.1 ms from a real start time,
// so only a multiple of that lands the same way on every run.
//
// Chromium's own clocks wobble (coarsened, and measured from a real start time), so the page reads this
// script's clock instead, through everything in prepare().
const { spawn } = require('child_process');
const fs        = require('fs');
const os        = require('os');
const path      = require('path');
const url       = require('url');

const kUsage        = 'Usage: browser_screenshot.js PAGE OUTPUT.png STEPS STEP_MS [WIDTH HEIGHT]\n  PAGE     a file path or URL\n  STEPS    how many timesteps to run, 1 or more\n  STEP_MS  each timestep in milliseconds, to at most 1 decimal, such as 16.7\n  WIDTH, HEIGHT  image size in pixels, default 1280 800';
const kStartEpochMs = 1700000000000;  // what the page's clock reads as the date at time 0
const kTotalMs      = 170000;
const kBeginOffset  = 3600000;        // frame times must lie after the page's real start, whenever that was
const kMaxNotes     = 20;

// Runs in the page before any of its scripts.
function prepare(startEpochMs, stepMs)
{
	let clock = 0;
	Object.defineProperty(window, '__beastClock', { set(value) { clock = value; }, get() { return clock; } });

	const micro = value => Math.round(value * 1000) / 1000;

	// performance.now, and the origin it counts from.
	Performance.prototype.now = function () { return clock; };
	Object.defineProperty(Performance.prototype, 'timeOrigin', { get() { return startEpochMs; } });

	// Date, with or without new. A date given as an argument is left as it is.
	const RealDate = Date;
	function CountedDate(...args)
	{
		if (!new.target)
			return new RealDate(startEpochMs + clock).toString();
		return Reflect.construct(RealDate, args.length > 0 ? args : [startEpochMs + clock], new.target);
	}
	Object.setPrototypeOf(CountedDate, RealDate);
	CountedDate.prototype             = RealDate.prototype;
	RealDate.prototype.constructor    = CountedDate;
	CountedDate.now                   = function () { return startEpochMs + clock; };
	window.Date                       = CountedDate;

	// requestAnimationFrame's timestamp, and its loop kept first so the offset below is fresh for the page's.
	const realFrame = window.requestAnimationFrame.bind(window);
	window.requestAnimationFrame = function (callback)
	{
		return realFrame(() => callback(clock));
	};

	// CSS animation time. Chromium's own wobbles by 0.1 ms and the picture is drawn from it, so correcting what
	// the page reads is not enough. Each running animation gets a counted position: where it was when first
	// seen running, plus the clock since then. At the start of every frame, before styles are worked out, its
	// real start time is set so that Chromium's position is the counted one.
	const timelineTime   = Object.getOwnPropertyDescriptor(AnimationTimeline.prototype, 'currentTime');
	const animationTime  = Object.getOwnPropertyDescriptor(Animation.prototype, 'currentTime');
	const animationStart = Object.getOwnPropertyDescriptor(Animation.prototype, 'startTime');
	const counted        = new WeakMap();
	const held           = new WeakMap();
	let   timelineGap    = 0;

	const position = (animation, entry) => entry.base + (clock - entry.seen) * animation.playbackRate;

	(function track()
	{
		const real = timelineTime.get.call(document.timeline);
		if (real !== null)
		{
			timelineGap = real - clock;
			for (const animation of document.getAnimations())
			{
				if (animation.playState === 'paused' && !animation.pending && held.has(animation))
				{
					animationTime.set.call(animation, held.get(animation));
				}
				else if (animation.playState === 'running' && animation.timeline === document.timeline && animationStart.get.call(animation) !== null)
				{
					let entry = counted.get(animation);
					if (!entry)
					{
						// Carrying on from a pause, or on its first frame running, when its position can only be
						// a whole number of steps.
						const grid  = stepMs * Math.abs(animation.playbackRate);
						const value = animationTime.get.call(animation);
						entry       = { base: held.has(animation) ? held.get(animation) : grid > 0 ? Math.round(value / grid) * grid : value, seen: clock };
						counted.set(animation, entry);
						held.delete(animation);
					}
					if (animation.playbackRate !== 0)
						animationStart.set.call(animation, real - position(animation, entry) / animation.playbackRate);
				}
			}
		}
		realFrame(track);
	})();

	Object.defineProperty(AnimationTimeline.prototype, 'currentTime',
	{
		configurable: true,
		get()
		{
			return this instanceof DocumentTimeline ? clock : timelineTime.get.call(this);
		}
	});

	// What the page reads follows the counted position; what it writes becomes the new one.
	Object.defineProperty(Animation.prototype, 'currentTime',
	{
		configurable: true,
		get()
		{
			const entry = counted.get(this);
			if (entry && this.playState === 'running')
				return micro(position(this, entry));
			if (held.has(this))
				return micro(held.get(this));
			const value = animationTime.get.call(this);
			return value === null ? null : micro(value);
		},
		set(value)
		{
			animationTime.set.call(this, value);
			held.delete(this);
			counted.delete(this);
			if (value !== null && this.playState === 'paused')
				held.set(this, value);
			else if (value !== null)
				counted.set(this, { base: value, seen: clock });
		}
	});

	Object.defineProperty(Animation.prototype, 'startTime',
	{
		configurable: true,
		get()
		{
			const entry = counted.get(this);
			if (entry && (this.playState === 'running' || this.playState === 'finished') && this.playbackRate !== 0)
				return micro(entry.seen - entry.base / this.playbackRate);
			const value = animationStart.get.call(this);
			return value === null ? null : micro(value - timelineGap);
		},
		set(value)
		{
			animationStart.set.call(this, value === null ? null : value + timelineGap);
			if (value === null)
				counted.delete(this);
			else
				counted.set(this, { base: (clock - value) * this.playbackRate, seen: clock });
		}
	});

	// Anything else that changes how an animation runs: it is counted again from its next running frame.
	for (const name of ['play', 'reverse', 'finish', 'cancel'])
	{
		const real = Animation.prototype[name];
		Animation.prototype[name] = function (...args)
		{
			counted.delete(this);
			if (name === 'finish' || name === 'cancel')
				held.delete(this);
			return real.apply(this, args);
		};
	}

	// A pause holds the exact counted position, and play carries on from it.
	const realPause = Animation.prototype.pause;
	Animation.prototype.pause = function ()
	{
		const entry = counted.get(this);
		if (entry && this.playState === 'running')
			held.set(this, position(this, entry));
		counted.delete(this);
		return realPause.call(this);
	};

	// A new rate keeps the position it had.
	function keepPosition(animation, change)
	{
		const entry = counted.get(animation);
		const held  = entry && animation.playState === 'running' ? position(animation, entry) : null;
		const value = change();
		if (held !== null)
			counted.set(animation, { base: held, seen: clock });
		return value;
	}
	const animationRate = Object.getOwnPropertyDescriptor(Animation.prototype, 'playbackRate');
	Object.defineProperty(Animation.prototype, 'playbackRate',
	{
		configurable: true,
		get() { return animationRate.get.call(this); },
		set(value) { keepPosition(this, () => animationRate.set.call(this, value)); }
	});
	const updateRate = Animation.prototype.updatePlaybackRate;
	Animation.prototype.updatePlaybackRate = function (value)
	{
		return keepPosition(this, () => updateRate.call(this, value));
	};

	for (const type of [window.AnimationEvent, window.TransitionEvent])
	{
		const elapsed = Object.getOwnPropertyDescriptor(type.prototype, 'elapsedTime');
		Object.defineProperty(type.prototype, 'elapsedTime', { configurable: true, get() { return micro(elapsed.get.call(this)); } });
	}

	// An event keeps the time it was first asked for.
	const eventTimes = new WeakMap();
	Object.defineProperty(Event.prototype, 'timeStamp',
	{
		configurable: true,
		get()
		{
			if (!eventTimes.has(this))
				eventTimes.set(this, clock);
			return eventTimes.get(this);
		}
	});

	// Randomness comes from one seeded generator.
	let seed = 0x5eed2024;
	function next()
	{
		seed = (seed + 0x6D2B79F5) | 0;
		let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
		t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
		return (t ^ (t >>> 14)) >>> 0;
	}
	Math.random = function () { return next() / 4294967296; };
	if (window.crypto)
	{
		Crypto.prototype.getRandomValues = function (array)
		{
			const bytes = new Uint8Array(array.buffer, array.byteOffset, array.byteLength);
			for (let i = 0; i < bytes.length; i++)
				bytes[i] = next() & 255;
			return array;
		};
		Crypto.prototype.randomUUID = function ()
		{
			const hex = n => Array.from({ length: n }, () => (next() & 15).toString(16)).join('');
			return `${hex(8)}-${hex(4)}-4${hex(3)}-a${hex(3)}-${hex(12)}`;
		};
	}
}

function fail(message)
{
	console.error(message);
	process.exit(1);
}

function parseArguments(argv)
{
	if (argv.length < 4 || argv.length === 5 || argv.length > 6)
		fail(kUsage);
	const [page, output, stepsText, stepText, widthText, heightText] = argv;
	const steps  = Number(stepsText);
	const stepUs = Math.round(Number(stepText) * 10) * 100;
	const width  = widthText  === undefined ? 1280 : Number(widthText);
	const height = heightText === undefined ? 800  : Number(heightText);
	if (!/\.png$/i.test(output))
		fail(`OUTPUT must end in .png.\n${kUsage}`);
	if (!Number.isInteger(steps) || steps < 1)
		fail(`STEPS must be a whole number, 1 or more.\n${kUsage}`);
	if (!/^\d+(\.\d)?$/.test(stepText) || stepUs < 1)
		fail(`STEP_MS must be above 0, to at most 1 decimal.\n${kUsage}`);
	if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1)
		fail(`WIDTH and HEIGHT must be whole numbers.\n${kUsage}`);

	let target = page;
	if (!/^[a-z][a-z0-9+.-]+:/i.test(page))
	{
		const file = path.resolve(page);
		if (!fs.existsSync(file))
			fail(`There is no file at ${file}.`);
		target = url.pathToFileURL(file).href;
	}
	return { target, output: path.resolve(output), steps, stepUs, stepText, width, height };
}

function findBrowser()
{
	const root = '/opt/ms-playwright';
	for (const folder of fs.readdirSync(root))
	{
		for (const candidate of ['chrome-headless-shell-linux64/chrome-headless-shell', 'chrome-linux/headless_shell'])
		{
			const file = path.join(root, folder, candidate);
			if (folder.startsWith('chromium_headless_shell-') && fs.existsSync(file))
				return file;
		}
	}
	return fail(`No headless browser under ${root}.`);
}

// A small DevTools client: send() answers with the result; nextEvent() resolves on the next such event.
async function connect(address, onEvent)
{
	const socket  = new WebSocket(address);
	const pending = new Map();
	const waiting = new Map();
	let   lastId  = 0;
	await new Promise((resolve, reject) =>
	{
		socket.onopen  = resolve;
		socket.onerror = () => reject(new Error('Could not connect to the browser.'));
	});
	socket.onmessage = event =>
	{
		const message = JSON.parse(event.data);
		if (message.id && pending.has(message.id))
		{
			const { resolve, reject } = pending.get(message.id);
			pending.delete(message.id);
			if (message.error)
				reject(new Error(message.error.message));
			else
				resolve(message.result);
		}
		else if (message.method)
		{
			onEvent(message.method, message.params);
			if (waiting.has(message.method))
			{
				const resolve = waiting.get(message.method);
				waiting.delete(message.method);
				resolve(message.params);
			}
		}
	};
	return {
		send: (method, params = {}, sessionId = undefined) => new Promise((resolve, reject) =>
		{
			const id = ++lastId;
			pending.set(id, { resolve, reject });
			socket.send(JSON.stringify({ id, method, params, sessionId }));
		}),
		nextEvent: method => new Promise(resolve => waiting.set(method, resolve))
	};
}

async function capture(args, address)
{
	const notes = [];
	const note  = text =>
	{
		if (notes.length < kMaxNotes && !text.includes('GL Driver Message'))
			notes.push(text);
	};
	const requests = new Map();
	const contexts = new Set();  // the page's frames, each with its own clock
	let   loaded   = false;
	const client   = await connect(address, (method, params) =>
	{
		if (method === 'Page.loadEventFired')
			loaded = true;
		else if (method === 'Runtime.executionContextCreated' && params.context.auxData?.isDefault)
			contexts.add(params.context.id);
		else if (method === 'Runtime.executionContextDestroyed')
			contexts.delete(params.executionContextId);
		else if (method === 'Runtime.executionContextsCleared')
			contexts.clear();
		else if (method === 'Runtime.exceptionThrown')
			note(`error: ${params.exceptionDetails.exception?.description ?? params.exceptionDetails.text}`);
		else if (method === 'Runtime.consoleAPICalled')
			note(`console ${params.type}: ${params.args.map(arg => arg.value ?? arg.description ?? '').join(' ')}`);
		else if (method === 'Network.requestWillBeSent')
			requests.set(params.requestId, params.request.url);
		else if (method === 'Network.loadingFailed')
			note(`failed to load: ${requests.get(params.requestId)} (${params.errorText})`);
		else if (method === 'Network.responseReceived' && params.response.status >= 400)
			note(`failed to load: ${params.response.url} (status ${params.response.status})`);
	});

	const { targetId }  = await client.send('Target.createTarget', { url: 'about:blank', width: args.width, height: args.height, enableBeginFrameControl: true });
	const { sessionId } = await client.send('Target.attachToTarget', { targetId, flatten: true });
	const page          = (method, params) => client.send(method, params, sessionId);

	await page('Page.enable');
	await page('Runtime.enable');
	await page('Network.enable');
	await page('Emulation.setVirtualTimePolicy', { policy: 'pause', initialVirtualTime: kStartEpochMs / 1000 });
	await page('Page.addScriptToEvaluateOnNewDocument', { source: `(${prepare.toString()})(${kStartEpochMs}, ${args.stepUs / 1000});` });

	// A page loads inside the steps, not before them: with time fully paused it never finishes, and each
	// budget below waits out any fetch it is running.
	const opened = await page('Page.navigate', { url: args.target });
	if (opened.errorText)
		throw new Error(`Could not open ${args.target}: ${opened.errorText}`);

	const base = Math.floor(parseFloat(fs.readFileSync('/proc/uptime', 'utf8')) * 1000) + kBeginOffset;
	let   last = null;
	for (let step = 1; step <= args.steps; step++)
	{
		const now     = step * args.stepUs / 1000;
		const expired = client.nextEvent('Emulation.virtualTimeBudgetExpired');
		for (const contextId of contexts)
			await page('Runtime.evaluate', { expression: `window.__beastClock = ${now}`, contextId }).catch(() => {});
		await page('Emulation.setVirtualTimePolicy', { policy: 'pauseIfNetworkFetchesPending', budget: args.stepUs / 1000, maxVirtualTimeTaskStarvationCount: 100 });
		await expired;
		last = await page('HeadlessExperimental.beginFrame', { frameTimeTicks: base + now, interval: args.stepUs / 1000, noDisplayUpdates: false, screenshot: step === args.steps ? { format: 'png' } : undefined });
	}
	if (!last.screenshotData)
		throw new Error('The browser returned no picture for the last frame.');

	fs.mkdirSync(path.dirname(args.output), { recursive: true });
	const picture = Buffer.from(last.screenshotData, 'base64');
	fs.writeFileSync(args.output, picture);
	console.log(`Saved ${args.output} (${args.width}x${args.height}, ${picture.length} bytes): ${args.steps} steps of ${args.stepText} ms, page time ${args.steps * args.stepUs / 1000} ms.`);
	if (!loaded)
		console.log('The page had not finished loading at the last step. Use more steps.');
	for (const text of notes)
		console.log(`Page ${text}`);
}

async function main()
{
	const args    = parseArguments(process.argv.slice(2));
	const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'beast-screenshot-'));
	const browser = spawn(findBrowser(), [
		'--no-sandbox', '--disable-dev-shm-usage', '--disable-gpu', '--enable-unsafe-swiftshader', '--use-angle=swiftshader-webgl', '--hide-scrollbars',
		'--enable-begin-frame-control', '--run-all-compositor-stages-before-draw', '--disable-new-content-rendering-timeout', '--disable-threaded-animation',
		'--disable-threaded-scrolling', '--disable-checker-imaging', '--disable-image-animation-resync',
		'--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'
	], { stdio: ['ignore', 'ignore', 'pipe'], detached: true });
	const exited = new Promise(resolve => browser.on('exit', resolve));

	let code = 0;
	try
	{
		const address = await new Promise((resolve, reject) =>
		{
			let text = '';
			browser.stderr.on('data', data =>
			{
				text += data;
				const found = text.match(/ws:\/\/\S+/);
				if (found)
					resolve(found[0]);
			});
			browser.on('error', reject);
			browser.on('exit', () => reject(new Error('The browser exited before it was ready.')));
		});
		const limit = new Promise((_, reject) => setTimeout(() => reject(new Error(`The capture did not finish within ${kTotalMs / 1000} seconds. Use fewer steps.`)), kTotalMs));
		await Promise.race([capture(args, address), limit]);
	}
	catch (error)
	{
		console.error(error.message);
		code = 1;
	}
	// The whole process group, helpers included, and only then the profile they hold open. The capture's
	// result stands either way: a profile left behind is only a leftover in /tmp.
	try
	{
		process.kill(-browser.pid, 'SIGKILL');
		await exited;
		fs.rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
	}
	catch (error)
	{
	}
	process.exit(code);
}

main();
