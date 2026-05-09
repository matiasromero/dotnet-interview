# Crunchloop Senior Engineer Challenge

## Overview

At Crunchloop, we value pragmatic problem-solving and the ability to work with existing systems while expanding their functionality. This post-interview challenge is designed to evaluate your skills in building integrations and synchronizing data between APIs, emphasizing resilience, clarity, and performance.

## The Task

The task involves enhancing the **Todo API** implementation with a synchronization mechanism. You will build on top of your existing work to enable seamless data synchronization between the local Todo API instance and an external API.

### Key Objectives

1. **Implement Synchronization Logic**:
   - Develop a mechanism to synchronize `TodoLists` and their associated `TodoItems` with an external API.
   - The external API endpoint documentation will be provided.
   - Synchronization should support:
     - Creating new `TodoList`s and `TodoItem`s locally when detected in the external API.
     - Propagating local changes to the external API.
     - Handling deletions gracefully.

2. **Resilience and Reliability**:
   - Ensure the synchronization process handles partial failures.
   - Implement retry mechanisms.

3. **Optimize Performance**:
   - Minimize API calls to the external API as much as possible.

4. **Error Handling and Logging**:
   - Implement meaningful error messages for API errors.
   - Provide detailed logs for debugging synchronization issues.

5. **Document Design Decisions**:
   - Document your design choices, trade-offs, and assumptions in a `NOTES.md` file.
   - Highlight areas for future improvement or possible edge cases.

## External API Details

The external API is documented using OpenAPI. Find the full documentation [here](./docs/README.md)

## Evaluation Criteria

We will evaluate your submission based on:

1. **Functionality**:
   - Does the synchronization work as intended?
   - Are edge cases and failure scenarios handled?

2. **Code Quality**:
   - Is the code clean, modular, and maintainable?
   - Are meaningful tests provided?

3. **Performance**:
   - Are unnecessary API calls avoided?
   - Is the implementation efficient for large datasets?

4. **Documentation**:
   - Are design decisions and assumptions clearly articulated in the `NOTES.md`.

## Deliverables

1. **Source Code**:
   - Include the synchronization logic in your solution.
   - Provide test cases that demonstrate your solution's correctness.

2. **Documentation**:
   - A `NOTES.md` file explaining your design decisions, trade-offs, and assumptions.

3. **Instructions**:
   - Steps to run your solution and execute synchronization.

## How to Use `NOTES.md`

The `NOTES.md` file is a critical part of your submission. It helps us understand the reasoning behind your implementation. Use it to describe the **why** behind your solution, not just the **how**.

### What to Include in `NOTES.md`

1. **High-Level Overview**:
   - Summarize your approach to solving the synchronization challenge.
   - Describe the structure of your solution and its major components.

2. **Key Design Decisions**:
   - Explain the choices you made during development.
   - Highlight trade-offs, such as balancing between simplicity and extensibility.

3. **Resilience and Error Handling**:
   - Describe how your solution handles different failure modes.

4. **Edge Cases**:
   - Identify and explain how you handled potential edge cases.

5. **Areas for Improvement**:
   - Highlight areas you would refine given more time or resources.

6. **Assumptions**:
   - Outline assumptions you made while implementing the solution.

## Notes

- Feel free to use libraries or frameworks of your choice, but be prepared to explain your decisions.
- Focus on delivering a functional solution. You are not expected to over-engineer but should provide thoughtful, well-structured code.
- This challenge is open-ended; creativity and well-reasoned approaches are encouraged.
- Feel free to suggest changes to the external api that would improve the performance/reliability of your solution.

We look forward to seeing your solution! Good luck!
