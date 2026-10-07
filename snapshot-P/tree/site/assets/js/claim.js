// "Done is a claim" (PRAXIS-SITE-04): progressive enhancement only.
// Without JavaScript every question, answer and the verdict are already on the
// page. With it, the answers are revealed one at a time after the visitor asks
// for them; prefers-reduced-motion reveals them all at once.
(() => {
  "use strict";

  const reduced = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const wait = (ms) => new Promise((resolve) => window.setTimeout(resolve, ms));

  const conceal = (nodes) => nodes.forEach((node) => node.setAttribute("hidden", ""));
  const reveal = (node) => node.removeAttribute("hidden");

  const announce = (live, answered, total) => {
    live.textContent = answered === total ? `All ${total} questions answered.` : `${answered} of ${total} questions answered.`;
  };

  const control = (label) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "button claim__control";
    button.textContent = label;
    return button;
  };

  const enhance = (claim) => {
    const steps = Array.from(claim.querySelectorAll(".claim__step"));
    const verdict = claim.querySelector(".claim__verdict");
    const live = document.createElement("p");
    live.className = "claim__progress";
    live.setAttribute("role", "status");
    const button = control("Interrogate the claim");
    const bar = document.createElement("div");
    bar.className = "claim__bar";
    bar.append(button, live);
    claim.querySelector(".claim__statement").after(bar);

    const reset = () => {
      conceal([...steps, verdict]);
      claim.classList.remove("is-proven");
      live.textContent = "";
      button.textContent = "Interrogate the claim";
    };

    const run = async () => {
      button.disabled = true;
      const pause = reduced() ? 0 : 420;
      await steps.reduce(
        (previous, step, index) =>
          previous.then(() => wait(pause)).then(() => {
            reveal(step);
            announce(live, index + 1, steps.length);
          }),
        Promise.resolve(),
      );
      await wait(pause);
      reveal(verdict);
      claim.classList.add("is-proven");
      button.textContent = "Ask again";
      button.disabled = false;
    };

    button.addEventListener("click", () => {
      reset();
      run();
    });

    claim.classList.add("is-enhanced");
    reset();
  };

  document.querySelectorAll("[data-claim]").forEach(enhance);
})();
