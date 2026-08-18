#!/usr/bin/env node

import { main } from "../lib/bootstrap.mjs";

process.exitCode = await main(process.argv.slice(2));
