[<AutoOpen>]
module NullCoalesce

let inline (|??) (l : 'a System.Collections.Generic.IEnumerable) (r : 'a list) = if (isNull l) then r  else (Seq.toList l)