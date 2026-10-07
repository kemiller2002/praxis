#!/usr/bin/env node

import { run } from "../lib/lifecycle-launcher.mjs";

process.exitCode = await run(process.argv.slice(2));
