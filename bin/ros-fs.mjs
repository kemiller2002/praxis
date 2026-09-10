#!/usr/bin/env node

import { run } from "../lib/ros-fs-launcher.mjs";

process.exitCode = await run(process.argv.slice(2));
