// Praxis web and hub: the one approved browser script (DF-ROS-2026-A058).
// It does two things and nothing else. A filter refinement replaces the
// current history entry instead of pushing one (SAF-URL-3), and "Copy link"
// puts this view's address on the clipboard (SAF-URL-10). It makes no network
// request, keeps no storage and evaluates no code. With JavaScript off every
// page works as before: forms submit and push, and the address field can be
// selected by hand. UrlEnhancementTests fails if this file gains a capability.
"use strict";
document.querySelectorAll("form[data-refine]").forEach(function (form) {
  form.addEventListener("submit", function (event) {
    event.preventDefault();
    var query = new URLSearchParams();
    new FormData(form).forEach(function (value, name) {
      if (typeof value === "string" && value.trim() !== "") query.append(name, value);
    });
    var text = query.toString();
    location.replace(form.getAttribute("action") + (text ? "?" + text : ""));
  });
});
document.querySelectorAll("button[data-copy]").forEach(function (button) {
  var field = document.getElementById(button.getAttribute("data-copy"));
  var status = document.getElementById(button.getAttribute("data-status"));
  if (!field || !status || !navigator.clipboard) return;
  button.hidden = false;
  button.addEventListener("click", function () {
    navigator.clipboard.writeText(field.value).then(function () {
      status.textContent = "Link copied.";
    }, function () {
      field.select();
      status.textContent = "Could not copy: the address is selected, copy it with your keyboard.";
    });
  });
});
