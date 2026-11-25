import chalk from "chalk";
import { Command } from "commander";
import express from "express";
import _ from "lodash";
import sharp from "sharp";

const program = new Command()
  .name("noodle-example")
  .option("-p, --port <port>", "port to listen on", "3000")
  .parse();

const app = express();
app.get("/", async (_request, response) => {
  // sharp loads a native module that came as a platform-specific package.
  const pixel = await sharp({
    create: { width: 1, height: 1, channels: 3, background: "#cb3837" },
  }).png().toBuffer();
  response.json({
    message: "Every dependency of this app came from Noodle.",
    words: _.words("noodles oodles of packages"),
    pngBytes: pixel.length,
  });
});

const port = Number(program.opts().port);
app.listen(port, () => {
  console.log(chalk.red(`Listening on http://localhost:${port}`));
});
