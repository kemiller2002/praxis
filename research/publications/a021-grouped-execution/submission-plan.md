# A021 Submission Plan: venues, requirements, timeline

Prepared 2026-10-07 by the venue/submission audit. Machine-readable facts are in [`data/venues.json`](data/venues.json).

## 0. How these facts were checked (read this first)

- Every direct fetch of an official host failed. The session egress proxy refused `conf.researchr.org`, `2027.msrconf.org`, `easychair.org`, `www.ieee.org` and `conferences.ieeeauthorcenter.ieee.org` (EGRESS_BLOCKED / CONNECT 403). The proxy rules say not to route around this.
- Each fact below therefore comes from a search-index extract tied to the listed official URL. All extracts were collected on 2026-10-07 using WebSearch with the domain filter set to the official hosts. Most key dates showed up the same way across two or more independent queries. Facts seen only once are marked (single).
- **Gate before registering the abstract:** a human must open each source URL in a browser and confirm the dates, the page limit and the `\documentclass` line. The weekday mismatches found on the MSR and LLM4Code pages (Section 1) show that extracts can carry errors.
- "not stated" means the fact did not appear in any extract of the official page. It does not mean the page lacks it.

## 1. Verified requirements tables

### 1a. SANER 2027 (IEEE; Richmond, VA, 9-12 Mar 2027). Submission: EasyChair `https://easychair.org/conferences/?conf=saner27tracks`

| Item | Agentic AI4SE Track | Short Papers & Posters (SP&P) | Research Track | Registered Report | Source (fetched 2026-10-07) |
|---|---|---|---|---|---|
| Exists / exact name | Yes: "Agentic AI4SE Track" (newly launched) | Yes: "Short Papers and Posters (SP&P) Track" | Yes: "Research Track" | Yes: "Registered Report (RR) Track" | track URLs below |
| Abstract (mandatory) | **Mon 19 Oct 2026** | **Mon 19 Oct 2026** | 21 Sep 2026 (PAST) | 30 Oct 2026 (single) | |
| Paper | **Fri 23 Oct 2026** | **Fri 23 Oct 2026** | 25 Sep 2026 (PAST) | 6 Nov 2026 (single) | |
| Timezone | AoE (UTC-12h) | AoE | AoE | not stated | home page |
| Notification | Tue 8 Dec 2026 | Tue 8 Dec 2026 | 1 Dec 2026 | not stated | |
| Camera-ready / author registration | Fri 8 Jan 2027 | Fri 8 Jan 2027 | Fri 8 Jan 2027 | not stated | |
| Page limit | 10 pages + up to 2 pages of references only | **6 pages total, including references and appendices** (posters: 2-page extended abstract) | 10 + 2 refs | 6 + 1 refs, strict (single) | |
| Appendices | not stated (assume they count toward the 10) | count (inclusive) | not stated | not stated | |
| Template | IEEE conference proceedings; `\documentclass[10pt,conference]{IEEEtran}`, no `compsoc`/`compsocconf` (single) | same (single) | IEEE conference proceedings | same (single) | |
| Review | Double-anonymous; desk-reject if non-compliant | Double-anonymous | Double-anonymous | n/a | |
| Anonymity rules | Omit names and affiliations; self-citations in third person; omit acknowledgments and other identifying info; anonymize links to code, data, tools, videos and supplementary material "whenever possible" | same policy (open science) | same | | Agentic track page |
| Named anonymizer (anonymous.4open.science / Zenodo) | not stated | not stated | not stated | | |
| Open science | Encouraged, not mandatory. Include a **"Data Availability" section after Conclusions**. If anonymizing is hard, say links follow at camera-ready. PC will not require full artifact availability at submission | Encouraged | Encouraged | | Agentic track page |
| AI-use rule | Must comply with **IEEE Policy on Authorship, including guidelines on generative AI**. No venue-specific extra rule found | not stated (IEEE applies) | IEEE Policy on Authorship | | Agentic track page |
| Dual submission | Must not be published, accepted, or concurrently under review anywhere. Chairs may share submission lists with overlapping venues and run plagiarism checks | same | same | | Research Track page |
| arXiv | not stated on official page | not stated | not stated | | |
| Supplementary material / size | not stated (only "anonymized links") | not stated | not stated | | |
| Short-paper option in track | not stated (use SP&P) | n/a | n/a | | |

Sources:
- Agentic AI4SE: https://conf.researchr.org/track/saner-2027/saner-2027-agentic-ai4se-track (fetched 2026-10-07, search extract)
- SP&P: https://conf.researchr.org/track/saner-2027/saner-2027-short-papers-and-posters-track (2026-10-07)
- Research: https://conf.researchr.org/track/saner-2027/saner-2027-papers (2026-10-07)
- RR: https://conf.researchr.org/track/saner-2027/saner-2027-registered-report-track (2026-10-07)
- Dates/home: https://conf.researchr.org/home/saner-2027 (2026-10-07)
- **SANER 2027 RENE: unverified.** One search summary said RENE exists with deadlines of 19/23 Oct 2026, but no SANER 2027 RENE URL came back (only the 2026 page). An "Early Research Achievement" track was also mentioned without a URL. Do not plan around either track until someone confirms it on the home page.

### 1b. MSR 2027 (Dublin, 26-27 Apr 2027, co-located with ICSE). Submission: HotCRP `https://msr2027.hotcrp.com/`

| Item | Technical Papers | Source (fetched 2026-10-07) |
|---|---|---|
| Abstract | **20 Oct 2026 AoE**. The page labels it "Monday", but 20 Oct 2026 is a Tuesday | https://2027.msrconf.org/dates |
| Paper | **23 Oct 2026 AoE**. The page labels it "Thursday", but 23 Oct 2026 is a Friday | https://2027.msrconf.org/dates |
| Early reject / rebuttal | 3 Dec 2026 / 4-8 Dec 2026 | dates page |
| Notification / camera-ready | 8 Jan 2027 / 26 Jan 2027 | dates page |
| Page limit | Full: 10 pages, including figures, tables and appendices, + 2 pages of references only. Short work-in-progress: 4 + 1 refs | https://2027.msrconf.org/track/msr-2027-technical-papers |
| Template | IEEE (changed from ACM last year): `\documentclass[10pt,conference]{IEEEtran}`, no compsoc/compsocconf. Formatting deviations can lead to desk rejection | track page |
| Review | Double-anonymous | track page |
| Open science | Authors are *expected* to share anonymized, curated data, source code and replication instructions unless barriers exist. Artifact anonymity is best effort | track page |
| AI use | GenAI cannot be an author. Use to create content is permitted but "must be fully disclosed in the Work". The wording says "ACM-published Work", likely left over from the ACM era; ask the chairs | track page |
| arXiv | Allowed, but the preprint must not say it was submitted to MSR 2027 | track page |
| Dual submission / supplementary size | not stated in extracts | |
| Registered reports | Track exists (https://2027.msrconf.org/track/msr-2027-registered-reports); dates not retrieved | |

### 1c. Fallbacks

| Venue | Deadline | Notification | Limit | Template | Review | Source (2026-10-07) |
|---|---|---|---|---|---|---|
| ICSE 2027 NIER | **23 Oct 2026 AoE** (abstract: not stated) | 18 Dec 2026 | 4 pages, including figures, tables and appendices, + 1 page refs, strict | `\documentclass[10pt,conference]{IEEEtran}`, no compsoc | Double-anonymous | https://conf.researchr.org/track/icse-2027/icse-2027-new-ideas-and-emerging-results--nier- |
| AGENT 2027 workshop @ ICSE | **27 Nov 2026** per AGENT page. **CONFLICT:** the ICSE workshops page gives a uniform workshop deadline of 13 Nov 2026 AoE | 11 Dec 2026 | Full 8 pages excl. refs; Short 5 pages excl. refs | IEEE proceedings (class options not stated) | **Single-anonymous** (single) | https://conf.researchr.org/home/icse-2027/agent-2027 ; https://conf.researchr.org/track/icse-2027/icse-2027-workshops |
| LLM4Code 2027 @ ICSE | 13 Nov 2026 (ICSE uniform date only) | 11 Dec 2026 | 8 / 5 pages (unverified) | not stated | not stated | **unverified**: no 2027 CfP URL found |
| EASE 2027 Research (Hanoi) | Abstract 15 Jan 2027, paper 22 Jan 2027 AoE (single) | 12 Mar 2027 | not stated | not stated | not stated | https://conf.researchr.org/track/ease-2027/ease-2027-papers |
| ESEM 2027, FSE 2027 workshops, AIware 2027 | not checked or no official 2027 page found | | | | | |

### 1d. IEEE generative-AI policy (applies to every IEEE venue above)

"The use of content generated by artificial intelligence (AI) in an article (including but not limited to text, figures, images, and code) shall be disclosed in the **acknowledgments section** of any article submitted to an IEEE publication. The AI system used shall be identified, and specific sections of the article that use AI-generated content shall be identified and accompanied by a brief explanation regarding the level at which the AI system was used." Editing and grammar-only use is generally outside the policy, but disclosure is recommended. AI systems cannot be authors.

Source: IEEE policy as reproduced at https://attend.ieee.org/slt-2026/authors-instructions/ and https://open.ieee.org/author-guidelines-for-artificial-intelligence-ai-generated-text/ (search extract, 2026-10-07). The IEEE Author Center itself was blocked by the proxy.

## 2. Changes against the previously recorded values (README.md)

| Field | Recorded | Verified | Change? |
|---|---|---|---|
| SANER Agentic AI4SE name | "Agentic AI4SE Track" | "Agentic AI4SE Track" | none |
| SANER abstract / paper | 2026-10-19 / 2026-10-23 AoE | same | none |
| SANER limit | 10 + 2 refs | 10 + 2 refs only | none |
| SANER review | double anonymous | double anonymous, desk-reject | none |
| MSR abstract / paper | 2026-10-20 / 2026-10-23 | same dates, but the official page's weekday labels are wrong | **flag only** |
| New facts | — | SANER notification 8 Dec. SANER template `[10pt,conference]` with no compsoc. MSR 2027 uses IEEE (not ACM). SANER requires a "Data Availability" section. SANER bars concurrent submission | additions |

## 3. Scope fit

**SANER Agentic AI4SE: strong fit.** CfP topics that match the study:
- "multi-agent workflows for software engineering, including coordination, delegation, negotiation, and conflict resolution". Grouped vs independent execution is a delegation/coordination strategy.
- "planning, memory, tool use, retrieval, execution, and feedback mechanisms for AI agents working with software artifacts". The study's mechanism is shared context.
- "benchmarking and evaluation of agentic AI4SE systems, including task design, metrics, reproducibility, reliability, cost, and longitudinal evaluation". This matches the resource/cost outcomes, the blind evaluation and the replication.
- "evidence about their benefits, limitations, and risks". The mixed local-correctness results fit here.
- The track's focus on "analysis, evolution, maintenance, and reengineering" is only partly met, because the cohort is feature work. Frame the architectural-drift outcome as an evolution/maintainability concern.

**SANER SP&P: good fit.** It takes the same topics as the Research Track and welcomes empirical work. The 6-page all-inclusive limit suits n=1 evidence.

**MSR Technical: weak fit.** MSR centres on data from software repositories. This study is a controlled execution experiment, not repository mining. Commits are artifacts, not the object of study, so reviewers are likely to question the fit.

**ICSE NIER: good fit for an "emerging results" framing.** But 4 pages is tight, and the deadline is the same day as SANER, so you must pick one.

**AGENT 2027: moderate-to-good fit.** It covers "evaluation, and operation of agentic AI systems ... empirical evidence, reusable methods, operational lessons". Its focus is SE *for* agentic systems, while this study is about operating agents for SE, which is close enough for a workshop. Review is single-anonymous, so the anonymization work becomes optional.

**SANER RR: poor fit.** The study has already been executed. It would only suit a new pre-registered multi-domain follow-up.

## 4. Recommendation

The evidence is thin: one cohort, one domain, n=1 run per arm in each of two executions, and no inferential statistics. A 10-page full-paper venue will be judged on generalisability, and the most likely reviewer verdict is "a case study presented as an evaluation".

**Dual-submission constraint:** SANER (Agentic AI4SE and SP&P), MSR and ICSE NIER all close on 23 Oct 2026, and SANER explicitly bars concurrent review. They are alternatives, not a sequence. Pick exactly one by the go/no-go gate.

1. **Primary: SANER 2027 Agentic AI4SE Track, full paper (10 + 2).** Choose this only if the README's blocking evidence gaps close by **Wed 14 Oct**: the claim is frozen to the narrow statement, the threats-to-validity section is complete, and the anonymized artifact is ready.
2. **Primary-alternative, same deadline: SANER 2027 SP&P short paper (6 pages, all-inclusive).** This is the *preferred* option if the gaps are not closed by 14 Oct. A 6-page "early results from a replicated blinded case study" paper matches the strength of the evidence, keeps the strongest scope fit and uses the same template, so the switch only means cutting text.
3. **Secondary (not recommended): MSR 2027 Technical.** Same deadline, weaker scope fit, and it cannot be submitted alongside SANER. Use it only if SANER is ruled out for some non-scope reason.
4. **Fallback A (take this route instead of SANER if the team wants more runs first): AGENT 2027 @ ICSE.** Deadline 27 Nov or 13 Nov 2026 (confirm which). An 8-page full or 5-page short paper; single-anonymous; IEEE-published. This gives 5-7 weeks to add runs per arm or a second domain, which removes the n=1 weakness. Note that SANER notification (8 Dec) comes after this deadline, so AGENT cannot be a post-rejection fallback for SANER.
5. **Fallback B (after a SANER rejection on 8 Dec): EASE 2027.** Abstract 15 Jan, paper 22 Jan 2027 (single extract). EASE is an evidence-based SE venue. Its Short Papers & Emerging Results track exists, but its dates have not been retrieved.

## 5. Exact LaTeX

```latex
\documentclass[10pt,conference]{IEEEtran}   % SANER / MSR / NIER: do NOT add compsoc or compsocconf
% Paper size: not stated by venue; keep IEEEtran default (US letter). Do not add a4paper unless the CfP says so.
% Get the current IEEE conference template (IEEE Author Center "Authoring Tools and Templates", or the Overleaf IEEE conference template).
% Remove all template guidance text before submission.
\author{\IEEEauthorblockN{Anonymous Author(s)}\IEEEauthorblockA{Anonymous Institution(s)}}
```
- Title 24pt and body 10pt are the template defaults. Do not change spacing, font sizes or margins; SANER and MSR treat deviations as grounds for desk rejection.
- Full paper: content must end by page 10, and pages 11-12 may hold only references. SP&P: everything, references included, must fit in 6 pages.
- Section order at the end: Conclusions, then **Data Availability** (SANER requirement), then the anonymized **AI-Generated Content Disclosure** (see Section 7), then References.

## 6. Anonymization checklist (from the SANER rules, plus the existing list in `reproducibility-and-anonymization.md`)

- [ ] No author names or affiliations anywhere, including the PDF metadata (`pdfinfo`; set `\hypersetup{pdfauthor={}}`; strip Overleaf/LaTeX producer info if it names anyone).
- [ ] Citations of the authors' own prior work, including the Praxis repository and any blog or preprint, are written in the third person ("Prior work [X] proposed...").
- [ ] No acknowledgments section (SANER requires its removal). The AI disclosure goes in a separately titled section with no names (Section 7).
- [ ] No direct links to the public Praxis repository, GitHub org, issue/PR URLs, session URLs or claude.ai links.
- [ ] The artifact is hosted on an anonymized mirror. anonymous.4open.science is the community norm, but **SANER does not name a service**. Alternatively, upload a Zenodo/OSF/figshare record with anonymous metadata. A non-anonymous Zenodo DOI page reveals creators, so do not use one.
- [ ] Commit SHAs may stay (the baseline is `8b4ffa39...`), but a SHA can be searched on GitHub and lead to the identifying public repo. Either replace SHAs with logical IDs in the paper and keep the mapping only in the artifact, or confirm the repo is not publicly indexed.
- [ ] Repo and package names (e.g. "Praxis") and file paths like `research/experiments/EX-ROS-...` are renamed or neutralized in the paper, figures and artifact.
- [ ] Screenshots, logs and JSON in the artifact are scrubbed of usernames, emails, org names, hostnames and absolute home paths.
- [ ] Archive filenames are neutral, and zip or git metadata does not carry author names (`git log` authors must be stripped if a git bundle is included).
- [ ] If any piece cannot be anonymized, the Data Availability section says it will be linked at camera-ready (SANER allows this).
- [ ] No preprint is posted on arXiv that is linked to the submission. SANER does not state an arXiv rule, so the conservative choice is not to post until after notification. Under MSR's rule a preprint is allowed but must not mention MSR.
- [ ] All citations exist and have been checked by hand, which matters especially because the draft was AI-assisted.

## 7. AI-disclosure requirement and placement

- **Rule:** the IEEE Policy on Authorship and its generative-AI guidelines, which SANER explicitly requires. Disclosure must (a) name the AI system(s), (b) name the specific sections with AI-generated content, and (c) briefly state the level of use. AI cannot be an author. Humans remain responsible for all content.
- **Placement:** IEEE says "acknowledgments section", but SANER's anonymity rules say to omit acknowledgments. Resolve this by including an **anonymized section titled "Acknowledgment of AI-Generated Content"** with no names, funders or affiliations, placed after Data Availability and before References. In the 10+2 format, assume it counts toward the 10 pages, because SANER does not say otherwise. Keep it to about 4-6 lines. If unsure, email the track chairs before 19 Oct.
- **Keep two disclosures separate.** (1) *Writing assistance* belongs in the disclosure section. (2) *AI agents as experimental subjects* (the models, versions, harness and settings) belong in the Methods section, not the disclosure.
- **Draft text (fill in the bracketed parts accurately):**
  > *Acknowledgment of AI-Generated Content.* [Model name, version] was used to draft text in Sections [I-VII], to generate [analysis scripts / table formatting] and to assist with literature summarisation. All AI-generated text and code were reviewed, edited and verified by the authors, who take full responsibility for the content. All references were checked manually against the original sources. The AI coding agents that are the *subjects* of the study are described separately in Section [Method].
- MSR (if chosen): "must be fully disclosed in the Work". Use the same section.

## 8. Timeline (working back from SANER, all AoE)

| Date (2026) | Days left | Milestone |
|---|---|---|
| Wed 7 Oct | T-16 | Human confirms Section 0 facts on the official pages. Decide whether the team is aiming at SANER or at AGENT/more runs |
| Fri 9 Oct | T-14 | Claim frozen (README "central claim"). Figures and tables final from existing data. No new analyses after this |
| Mon 12 Oct | T-11 | Complete draft (10+2 format), including threats to validity and Data Availability |
| **Wed 14 Oct** | T-9 | **Go/no-go gate:** full paper (Agentic AI4SE) vs 6-page SP&P vs defer to AGENT (27 Nov). Choose one venue only |
| Thu 15 Oct | T-8 | Anonymized artifact built and uploaded (anonymous mirror). Link tested from a logged-out browser |
| Fri 16 Oct | T-7 | Internal blind read by a non-author; citation verification pass |
| **Sat 17 Oct** | T-6 | Create the EasyChair account and register the **abstract + all authors + conflicts** (2 days before the hard date) |
| **Mon 19 Oct** | T-4 | **Hard abstract deadline** (SANER mandatory abstract). MSR's would be Tue 20 Oct |
| Tue 20-Wed 21 Oct | T-3 | Page-limit, PDF-metadata and anonymity checks; template compliance check (no compsoc, no spacing hacks) |
| Thu 22 Oct | T-1 | **Upload final PDF** (one day of buffer) |
| **Fri 23 Oct** | T-0 | Hard paper deadline (23:59 AoE = Sat 24 Oct 11:59 UTC) |
| Tue 8 Dec | | SANER notification; if rejected, revise for EASE 2027 (abstract 15 Jan 2027) |

## 9. Risks

1. **Time:** 12 days to the mandatory abstract and 16 to the paper. A missed abstract registration means you cannot submit at all.
2. **Thin evidence:** with n=1 per arm and a single domain, a full-paper rejection is likely if reviewers read it as an evaluation. Mitigate with the SP&P format or an honest case-study framing with explicit mixed results.
3. **Unverified facts:** every value comes from search extracts because the official hosts were blocked. The MSR and LLM4Code weekday labels conflict with the calendar, and the AGENT and ICSE workshop deadlines conflict (27 Nov vs 13 Nov).
4. **Dual submission:** SANER, MSR and NIER cannot be held concurrently. Withdrawing from one to submit to another after the deadline is not possible.
5. **Anonymity leaks:** the public Praxis repo, commit SHAs, path names (`EX-ROS-...`) and AI session URLs can all identify the authors. Any of them can lead to a desk rejection.
6. **AI disclosure vs anonymity:** IEEE's "acknowledgments" placement conflicts with SANER's "omit acknowledgments" rule. Use the anonymized section and confirm with the chairs if unsure.
7. **AI-drafted text:** reviewers and checking tools may flag it. Disclose it fully, and verify every citation and number by hand.
8. **Page budget:** the disclosure and Data Availability sections probably count toward the 10 pages (or the 6 in SP&P).
9. **Scope reading:** the Agentic AI4SE track stresses analysis, evolution, maintenance and reengineering. Explicitly position architectural drift as an evolution/maintainability outcome.
