# Create Camp AIR DOCX Document

This prompt converts a markdown file into a professionally formatted Camp AIR DOCX document.

## Usage

Reference this prompt file and the markdown file you want to convert:

```
#file:CreateAIRDoc.md #file:path/to/your-document.md
```

## What This Does

1. **Cleans the markdown** - Fixes any formatting issues in the source file
2. **Creates formatted DOCX** with:
   - Camp AIR logo header (from `docs/images/CampAIR_Logo.png`)
   - Three line breaks for spacing
   - Right-aligned blue "CAMP AIR" label in all caps
   - Page break
   - Automatic table of contents
   - Page break
   - Full document content with images embedded

## Process

When you use this prompt, I will:

1. Use the `scripts/md-to-campair-docx.py` Python script to convert to DOCX
2. The script automatically:
   - Adds Camp AIR logo header centered at the top
   - Adds right-aligned blue "CAMP AIR" label
   - Inserts page break and table of contents
   - Converts all markdown content with proper formatting
   - Embeds images from relative paths
   - Creates the DOCX in the same directory as the source markdown

## Command

```powershell
python "scripts/md-to-campair-docx.py" "path/to/document.md" --logo "docs/images/CampAIR_Logo.png"
```

## Output

The DOCX file will be created at:
`same-directory-as-markdown/filename.docx`

## Notes

- Requires Python with `python-docx` and `markdown` packages installed
- The script auto-detects the logo if located at `../images/CampAIR_Logo.png` relative to the markdown file
- After opening in Word, press Ctrl+A then F9 to update the Table of Contents
- The DOCX is ready for professional use with proper formatting

MANDATORY: If an error occurs on generation, DO NOT try an alternative approach that won't have the content listed here. Fix the problem with the correct approach!