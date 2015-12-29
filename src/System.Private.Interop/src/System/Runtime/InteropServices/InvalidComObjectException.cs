// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

/*=============================================================================
**
** Class: InvalidComObjectException
**
** Purpose: This exception is thrown when an invalid COM object is used. This
**            happens when a the __ComObject type is used directly without
**            having a backing class factory.
**
=============================================================================*/

using System;

namespace System.Runtime.InteropServices
{
    public class InvalidComObjectException : Exception
    {
        public InvalidComObjectException()
            : base(SR.Arg_InvalidComObjectException)
        {
            HResult = __HResults.COR_E_INVALIDCOMOBJECT;
        }

        public InvalidComObjectException(String message)
            : base(message)
        {
            HResult = __HResults.COR_E_INVALIDCOMOBJECT;
        }

        public InvalidComObjectException(String message, Exception inner)
            : base(message, inner)
        {
            HResult = __HResults.COR_E_INVALIDCOMOBJECT;
        }
    }
}
