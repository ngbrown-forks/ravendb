import { BaseAutocompleteProvider, filterTokens, QuerySource } from "./baseProvider";
import { AUTOCOMPLETE_META, AUTOCOMPLETE_SCORING, AutocompleteContext, AutocompleteProvider } from "./common";
import { Scanner } from "../scanner";
import { RqlParser } from "../RqlParser";
import { AutocompleteUtils } from "../autocompleteUtils";
import { CandidateRule } from "antlr4-c3/out/src/CodeCompletionCore";
import { LiteralContext } from "../generated/BaseRqlParser";

export class AutoCompleteFields extends BaseAutocompleteProvider implements AutocompleteProvider {
    
    findFieldPrefix(writtenPart: string, scanner: Scanner): string {
        scanner.push();
        
        const parts: string[] = [];
        
        try {
            while (scanner.lookBack() === RqlParser.DOT) {
                if (!scanner.previous()) {
                    break;
                }
                // at dot
                // check for []
                if (scanner.lookBack() === RqlParser.CL_Q) {
                    scanner.previous();
                    if (scanner.lookBack() === RqlParser.OP_Q) {
                        scanner.previous();
                    } else {
                        // unexpected
                        break;
                    }
                }
                
                // at literal / string
                if (scanner.previous()) {
                    switch (scanner.tokenType()) {
                        case RqlParser.STRING:
                            parts.unshift(AutocompleteUtils.unquote(scanner.tokenText()));
                            break;
                        case RqlParser.WORD:
                            parts.unshift(scanner.tokenText());
                            break;
                        default:
                            break;
                    }
                } else {
                    break; 
                }
            }
        } finally {
            scanner.pop();
        }
        
        return parts.join(".");
    }
    
    static resolvePrefix(fieldPrefix: string, ctx: AutocompleteContext): { source: QuerySource; sourceName: string; prefix: string; pathWoPrefix: string } {
        const fromAlias = ctx.queryMetaInfo.fromAlias;
        if (fromAlias && (fieldPrefix.startsWith(fromAlias + ".") || fieldPrefix === fromAlias)) { 
            // strip from alias
            return {
                source: ctx.queryMetaInfo.querySourceType,
                sourceName: ctx.queryMetaInfo.querySourceName,
                prefix: fromAlias,
                pathWoPrefix: fieldPrefix.substring(fromAlias.length + 1)
            }
        }
        
        // looks like no prefix found - just return original source gathered from 'from' stmt
        // + leave field prefix untouched
        return {
            source: ctx.queryMetaInfo.querySourceType,
            sourceName: ctx.queryMetaInfo.querySourceName,
            prefix: "",
            pathWoPrefix: fieldPrefix
        }
    }
    
    private collectIncludeFields(rule: CandidateRule, fieldPrefix: string, ctx: AutocompleteContext): autoCompleteWordList[] {
        if (fieldPrefix) {
            return [];
        }
        
        // only suggest when inside include
        if (!rule.ruleList.find(x => x === RqlParser.RULE_includeStatement)) {
            return [];
        }
        
        const results: autoCompleteWordList[] = [
            {
                value: "counters(",
                caption: "counters(name)",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            },
            {
                value: "timeseries(",
                caption: "timeseries(name, from?, to?)",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            },
            {
                value: "cmpxchg(",
                caption: "cmpxchg(name)",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            },
            {
                value: "highlight() ",
                caption: "highlight(field, fragmentLength, fragmentCount)",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            }
        ];
        
        let hasTimings = false;
        let hasExplanations = false;
        
        const includeStmt = ctx.parseTree.includeStatement();
        for (const child of includeStmt.children) {
            if (child && child instanceof LiteralContext) {
                if (child.text.toLocaleLowerCase().startsWith("timings(")) {
                    hasTimings = true;
                }
                if (child.text.toLocaleLowerCase().startsWith("explanations(")) {
                    hasExplanations = true;
                }
            }
        }

        if (!hasTimings) {
            results.push( {
                value: "timings() ",
                caption: "timings()",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            });
        }

        if (!hasExplanations) {
            results.push({
                value: "explanations(",
                caption: "explanations()",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            });
        }
        
        return results;
    }
    
    private collectGroupByFields(rule: CandidateRule, fieldPrefix: string, ctx: AutocompleteContext): autoCompleteWordList[] {
        if (fieldPrefix) {
            return [];
        }
        
        // only suggest in select stmt + we require query to have group by stmt
        if (!rule.ruleList.find(x => x === RqlParser.RULE_selectStatement)) {
            return [];
        }
        
        // and we need to have group by statement
        if (!ctx.parseTree.groupByStatement()) {
            return [];
        }
        
        return [
            {
                value: "key()",
                caption: "key()",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            },
            {
                value: "count()",
                caption: "count()",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            },
            {
                value: "sum(",
                caption: "sum()",
                meta: AUTOCOMPLETE_META.function,
                score: AUTOCOMPLETE_SCORING.function
            }
        ];
    }
    
    async collectAsync(ctx: AutocompleteContext): Promise<autoCompleteWordList[]> {
        const { candidates, scanner, writtenText, queryMetaInfo } = ctx;
        if (candidates.rules.has(RqlParser.RULE_variable)) {
            const rule = candidates.rules.get(RqlParser.RULE_variable);
            
            if (rule.ruleList.indexOf(RqlParser.RULE_limitStatement) !== -1) {
                // don't suggest fields in limit/offset
                return [];
            }
            
            if (rule.startTokenIndex < scanner.tokenIndex && scanner.lookBack() !== RqlParser.DOT) {
                return [];
            }
            
            const fieldPrefix = this.findFieldPrefix(writtenText, scanner);
            
            const { source, sourceName, pathWoPrefix, prefix } = AutoCompleteFields.resolvePrefix(fieldPrefix, ctx);
            
            const fields = await this.getPossibleFields(source, sourceName, pathWoPrefix);

            const shouldAddPrefix = !writtenText && !fieldPrefix && (prefix || queryMetaInfo.fromAlias || "");
            const effectivePrefixToUse = prefix || queryMetaInfo.fromAlias || "";
            
            const groupByFunctions = this.collectGroupByFields(rule, fieldPrefix, ctx);
            
            const filteredFields = filterTokens(writtenText, fields).map(field => ({
                meta: AUTOCOMPLETE_META.field,
                score: AUTOCOMPLETE_SCORING.field,
                caption: shouldAddPrefix ? effectivePrefixToUse + "." + field : field,
                value: shouldAddPrefix ? effectivePrefixToUse + "." + field : field
            }));
            
            const filteredGroupByFunctions = filterTokens(writtenText, groupByFunctions, x => x.value);
            
            const includeFunctions = this.collectIncludeFields(rule, fieldPrefix, ctx);
            
            const filteredIncludeFunctions = filterTokens(writtenText, includeFunctions, x => x.value);
            
            return [...filteredFields, ...filteredGroupByFunctions, ...filteredIncludeFunctions];
        }
        
        return [];
    }
}