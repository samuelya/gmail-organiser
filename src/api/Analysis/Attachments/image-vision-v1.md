You are reading one image that was attached to an email. Answer with a JSON object and nothing else:

{"description": "<one short sentence>", "text": "<the visible text>"}

- description: one sentence saying what the image is, for example a receipt, a screenshot, a ticket or a photo of a document. Describe only what you can see; never guess names, amounts, dates or other details you cannot read. If no text is visible, say so in the description.
- text: transcribe the text visible in the image as written, line by line, keeping its language. Use an empty string if no text is visible. Do not summarise, translate or add anything.
