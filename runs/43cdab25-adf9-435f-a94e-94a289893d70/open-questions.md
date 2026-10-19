# Open questions

Requirement: Make the links more secure and give marketing better analytics.

Answer each question on its "Answer:" line with one or more option letters (for example "A" or "A, C"), or "Other: <your answer>". Save the file, then approve. Changing an answer later re-plans the stages that depend on it.

## Q1. What should "more secure" mean for links?
Why it matters: each option changes a different part of the system (creation validation, code generation, or authentication) and has a different cost.
- A. Block malicious or phishing target URLs when a link is created (screen against a denylist)
- B. Make short codes harder to guess (longer codes, more entropy)
- C. Require an API key to create links
Answer: C

## Q2. What does "better analytics" mean for marketing?
Why it matters: clicks per day and top referrers already exist in /stats; the new work depends on which decision marketing wants to make.
- A. Breakdown by visitor country
- B. CSV export of click events for a date range
- C. Breakdown by UTM campaign parameters (utm_source, utm_medium, utm_campaign)
Answer: B

## Q3. May we store more data about visitors?
Why it matters: today only a SHA-256 hash of the IP is stored; some analytics options need more.
- A. No: keep storing only the IP hash
- B. Yes, but only a coarse country derived at click time; the raw IP is never stored
Answer: A

## Answers
- Q1: C
- Q2: B
- Q3: A
